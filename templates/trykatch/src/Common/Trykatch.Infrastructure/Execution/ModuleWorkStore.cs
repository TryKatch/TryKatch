using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Execution;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Execution;

internal interface IModuleWorkStore
{
    Task<ModuleWorkClaim> ClaimAsync(ModuleExecutionRequest request, CancellationToken cancellationToken);
    Task<bool> FinishAsync(Guid workId, Guid leaseId, ModuleExecutionOutcome outcome, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid workId, Guid leaseId, CancellationToken cancellationToken);
}

internal sealed record ModuleWorkClaim(
    ModuleWorkItem? Work,
    Guid? LeaseId,
    ModuleExecutionOutcome? Outcome,
    bool RecoveryOnly = false);

internal sealed class ModuleWorkStore : IModuleWorkStore
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(5);

    private readonly string connectionString;

    public ModuleWorkStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        connectionString = RuntimeDatabaseConnectionContract.Get(
            configuration,
            RuntimeDatabaseConnectionContract.WorkCoordinator);
    }

    public async Task<ModuleWorkClaim> ClaimAsync(
        ModuleExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using ModuleWorkDbContext db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        List<Guid> lockedIds = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value"
            FROM infrastructure.module_work_items
            WHERE "Id" = {request.WorkId}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (lockedIds.Count == 0)
        {
            bool exists = await db.ModuleWorkItems
                .AnyAsync(work => work.Id == request.WorkId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return exists
                ? PausedClaim()
                : FailedClaim(null, "work_not_found");
        }

        ModuleWorkItem workItem = await db.ModuleWorkItems
            .SingleAsync(work => work.Id == lockedIds[0], cancellationToken);
        if (!string.Equals(workItem.OperationKey, request.ExpectedOperationKey, StringComparison.Ordinal)
            || workItem.OperationVersion != request.ExpectedOperationVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return FailedClaim(workItem, "request_mismatch");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (workItem.State)
        {
            case "completed":
                await transaction.CommitAsync(cancellationToken);
                return OutcomeClaim(workItem, ModuleExecutionState.AlreadyCompleted);
            case "denied":
                await transaction.CommitAsync(cancellationToken);
                return OutcomeClaim(workItem, ModuleExecutionState.Denied, workItem.LastFailureCode);
            case "terminal":
                await transaction.CommitAsync(cancellationToken);
                return OutcomeClaim(workItem, ModuleExecutionState.TerminalFailure, workItem.LastFailureCode);
            case "leased" when workItem.LeaseUntilUtc is not null && workItem.LeaseUntilUtc > now:
                await transaction.CommitAsync(cancellationToken);
                return PausedClaim(workItem);
        }

        if (workItem.State is not ("queued" or "paused" or "leased"))
        {
            await transaction.CommitAsync(cancellationToken);
            return FailedClaim(workItem, "invalid_work_state");
        }

        if ((workItem.DueAtUtc is not null && workItem.DueAtUtc > now)
            || (workItem.NextAttemptAtUtc is not null && workItem.NextAttemptAtUtc > now))
        {
            await transaction.CommitAsync(cancellationToken);
            return PausedClaim(workItem);
        }

        bool recoveryOnly = workItem.Attempts >= MaximumAttempts;

        Guid leaseId = Guid.NewGuid();
        workItem.State = "leased";
        if (!recoveryOnly) workItem.Attempts++;
        workItem.LeaseId = leaseId;
        workItem.LeaseUntilUtc = now + LeaseDuration;
        workItem.NextAttemptAtUtc = null;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ModuleWorkClaim(workItem, leaseId, null, recoveryOnly);
    }

    public async Task<bool> FinishAsync(
        Guid workId,
        Guid leaseId,
        ModuleExecutionOutcome outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        (string state, DateTimeOffset? nextAttemptAtUtc) = outcome.State switch
        {
            ModuleExecutionState.Completed or ModuleExecutionState.AlreadyCompleted => ("completed", (DateTimeOffset?)null),
            ModuleExecutionState.Denied => ("denied", (DateTimeOffset?)null),
            ModuleExecutionState.Paused => ("paused", (DateTimeOffset?)null),
            ModuleExecutionState.RetryableFailure =>
                ("queued", Later(outcome.RetryAtUtc, now + MinimumRetryDelay)),
            ModuleExecutionState.TerminalFailure => ("terminal", (DateTimeOffset?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), "Unknown module execution outcome state.")
        };

        await using ModuleWorkDbContext db = CreateDbContext();
        int updated = await db.ModuleWorkItems
            .Where(work => work.Id == workId && work.State == "leased" && work.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(work => work.Attempts, work => outcome.State == ModuleExecutionState.Paused ? work.Attempts - 1 : work.Attempts)
                .SetProperty(work => work.State, state)
                .SetProperty(work => work.LastFailureCode, outcome.FailureCode)
                .SetProperty(work => work.NextAttemptAtUtc, nextAttemptAtUtc)
                .SetProperty(work => work.LeaseId, (Guid?)null)
                .SetProperty(work => work.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken);

        return updated == 1;
    }

    public async Task ReleaseAsync(Guid workId, Guid leaseId, CancellationToken cancellationToken)
    {
        await using ModuleWorkDbContext db = CreateDbContext();
        await db.ModuleWorkItems
            .Where(work => work.Id == workId && work.State == "leased" && work.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(work => work.State, "queued")
                .SetProperty(work => work.LeaseId, (Guid?)null)
                .SetProperty(work => work.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken);
    }

    private ModuleWorkDbContext CreateDbContext()
    {
        DbContextOptions<ModuleWorkDbContext> options = new DbContextOptionsBuilder<ModuleWorkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ModuleWorkDbContext(options);
    }

    private static DateTimeOffset Later(DateTimeOffset? requested, DateTimeOffset minimum) =>
        requested is not null && requested > minimum ? requested.Value : minimum;

    private static ModuleWorkClaim PausedClaim(ModuleWorkItem? workItem = null) =>
        OutcomeClaim(workItem, ModuleExecutionState.Paused);

    private static ModuleWorkClaim FailedClaim(ModuleWorkItem? workItem, string failureCode) =>
        OutcomeClaim(workItem, ModuleExecutionState.TerminalFailure, failureCode);

    private static ModuleWorkClaim OutcomeClaim(
        ModuleWorkItem? workItem,
        ModuleExecutionState state,
        string? failureCode = null) =>
        new(workItem, null, new ModuleExecutionOutcome(state, failureCode));
}

internal sealed class ModuleWorkDbContext(DbContextOptions<ModuleWorkDbContext> options) : DbContext(options)
{
    internal DbSet<ModuleWorkItem> ModuleWorkItems => Set<ModuleWorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        ModuleWorkPersistence.Configure(modelBuilder);
}
