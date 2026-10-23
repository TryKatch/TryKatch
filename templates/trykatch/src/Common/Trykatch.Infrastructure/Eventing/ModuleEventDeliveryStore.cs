using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Execution;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Eventing;

internal sealed record ModuleEventDeliveryClaim(ModuleEventDelivery? Delivery, Guid? LeaseId,
    ModuleExecutionOutcome? Outcome, bool RecoveryOnly = false);

internal sealed record ModuleEventBacklog(long Pending, long Retrying, long Terminal, DateTimeOffset? Oldest);

internal interface IModuleEventDeliveryStore
{
    Task<ModuleEventDeliveryClaim?> ClaimAsync(Guid? deliveryId, CancellationToken cancellationToken);
    Task<bool> FinishAsync(Guid deliveryId, Guid leaseId, ModuleExecutionOutcome outcome, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid deliveryId, Guid leaseId, CancellationToken cancellationToken);
    Task<ModuleEventBacklog> ReadBacklogAsync(CancellationToken cancellationToken);
}

internal sealed class ModuleEventDeliveryStore(IConfiguration configuration, TimeProvider clock) : IModuleEventDeliveryStore
{
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly string connection = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);

    public async Task<ModuleEventDeliveryClaim?> ClaimAsync(Guid? deliveryId, CancellationToken cancellationToken)
    {
        await using ModuleEventDbContext db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        ModuleEventDelivery? delivery;
        if (deliveryId is { } id)
        {
            delivery = await db.Deliveries.FromSqlInterpolated($"""
                SELECT * FROM infrastructure.module_event_deliveries WHERE "Id" = {id}
                FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            delivery = await db.Deliveries.FromSqlInterpolated($"""
                SELECT * FROM infrastructure.module_event_deliveries
                WHERE ("State" IN ('queued', 'paused') OR ("State" = 'leased' AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" <= {now})))
                  AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now})
                ORDER BY COALESCE("NextAttemptAtUtc", "CreatedAtUtc"), "CreatedAtUtc", "Id" FOR UPDATE SKIP LOCKED LIMIT 1
                """).SingleOrDefaultAsync(cancellationToken);
        }
        if (delivery is null) return null;
        if (delivery.State is "completed" or "terminal")
            return new(delivery, null, new(delivery.State == "completed" ? ModuleExecutionState.AlreadyCompleted
                : ModuleExecutionState.TerminalFailure, delivery.LastFailureCode));
        if (delivery.State == "leased" && delivery.LeaseUntilUtc > now || delivery.NextAttemptAtUtc > now)
            return new(delivery, null, new(ModuleExecutionState.Paused, "delivery_not_ready"));
        if (delivery.State is not ("queued" or "paused" or "leased"))
            return new(delivery, null, new(ModuleExecutionState.TerminalFailure, "delivery_state_invalid"));

        bool recoveryOnly = delivery.Attempts >= MaximumAttempts;
        Guid lease = Guid.NewGuid();
        delivery.State = "leased";
        delivery.LeaseId = lease;
        delivery.LeaseUntilUtc = now.AddMinutes(2);
        delivery.NextAttemptAtUtc = null;
        if (!recoveryOnly) delivery.Attempts++;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(delivery, lease, null, recoveryOnly);
    }

    public async Task<bool> FinishAsync(Guid deliveryId, Guid leaseId, ModuleExecutionOutcome outcome, CancellationToken cancellationToken)
    {
        DateTimeOffset retry = clock.GetUtcNow() + RetryDelay;
        if (outcome.RetryAtUtc > retry) retry = outcome.RetryAtUtc.Value;
        string state = outcome.State switch
        {
            ModuleExecutionState.Completed or ModuleExecutionState.AlreadyCompleted => "completed",
            ModuleExecutionState.Denied or ModuleExecutionState.TerminalFailure => "terminal",
            ModuleExecutionState.Paused => "paused",
            ModuleExecutionState.RetryableFailure => "queued",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        DateTimeOffset? next = state is "queued" or "paused" ? retry : null;
        await using ModuleEventDbContext db = CreateContext();
        return await db.Deliveries.Where(row => row.Id == deliveryId && row.State == "leased" && row.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, state)
                .SetProperty(row => row.Attempts, row => state == "paused" ? row.Attempts - 1 : row.Attempts)
                .SetProperty(row => row.NextAttemptAtUtc, next)
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(row => row.LastFailureCode, outcome.FailureCode), cancellationToken) == 1;
    }

    public async Task ReleaseAsync(Guid deliveryId, Guid leaseId, CancellationToken cancellationToken)
    {
        await using ModuleEventDbContext db = CreateContext();
        await db.Deliveries.Where(row => row.Id == deliveryId && row.State == "leased" && row.LeaseId == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, "queued")
                .SetProperty(row => row.NextAttemptAtUtc, (DateTimeOffset?)(clock.GetUtcNow() + RetryDelay))
                .SetProperty(row => row.LeaseId, (Guid?)null)
                .SetProperty(row => row.LeaseUntilUtc, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task<ModuleEventBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        await using ModuleEventDbContext db = CreateContext();
        var pending = db.Deliveries.Where(row => row.State != "completed" && row.State != "terminal");
        return new(await pending.LongCountAsync(cancellationToken),
            await pending.LongCountAsync(row => row.State == "queued" && row.Attempts > 0, cancellationToken),
            await db.Deliveries.LongCountAsync(row => row.State == "terminal", cancellationToken),
            await pending.Select(row => (DateTimeOffset?)row.CreatedAtUtc).MinAsync(cancellationToken));
    }

    internal ModuleEventDbContext CreateContext() => new(new DbContextOptionsBuilder<ModuleEventDbContext>().UseNpgsql(connection).Options);
}

internal sealed class ModuleEventDbContext(DbContextOptions<ModuleEventDbContext> options) : DbContext(options)
{
    internal DbSet<ModuleEventDelivery> Deliveries => Set<ModuleEventDelivery>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => ModuleWorkPersistence.Configure(modelBuilder);
}
