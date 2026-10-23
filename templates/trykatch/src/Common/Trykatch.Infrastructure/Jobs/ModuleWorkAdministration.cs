using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Jobs;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Jobs;

internal sealed class ModuleWorkAdministration(
    IConfiguration configuration, IPlatformAccessDirectory accessDirectory, TimeProvider clock) : IModuleWorkAdministration
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    { "queued", "leased", "paused", "completed", "denied", "terminal" };
    private static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "attempts_exhausted", "automatic_retry_not_supported", "completion_mismatch", "execution_kind_invalid",
        "execution_identity_invalid", "execution_timeout", "handler_failed", "membership_required", "module_unavailable",
        "operation_adapter_unavailable", "operation_unavailable", "organization_inactive", "organization_unavailable",
        "payload_invalid", "permission_denied", "placement_not_supported", "placement_unavailable", "resource_denied",
        "runtime_database_contract_invalid", "schedule_registration_missing", "schedule_registration_invalid", "source_event_invalid", "work_context_invalid",
        "work_execution_failed", "denial_audit_failed", "acknowledgement_failed", "lease_lost"
    };

    public async Task<Result<PagedResult<ModuleWorkView>>> ListAsync(Guid actorId, int page, int pageSize, string? state,
        CancellationToken cancellationToken = default)
    {
        EffectivePlatformAccess access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        if (!access.HasPermission(PlatformPermissions.JobsRead))
            return Result.Failure<PagedResult<ModuleWorkView>>("forbidden", "Current platform access cannot inspect module work.");
        if (state is not null && !States.Contains(state))
            return Result.Failure<PagedResult<ModuleWorkView>>("invalid_request", "Work state is invalid.");
        await using ModuleWorkDbContext db = CreateContext();
        int boundedPage = Math.Clamp(page, 1, 10_000), boundedSize = Math.Clamp(pageSize, 1, 100);
        IQueryable<ModuleWorkItem> query = db.ModuleWorkItems.AsNoTracking();
        if (state is not null) query = query.Where(work => work.State == state);
        long count = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(work => work.CreatedAtUtc).ThenByDescending(work => work.Id)
            .Skip((boundedPage - 1) * boundedSize).Take(boundedSize)
            .Select(work => new
            {
                work.Id, work.OrganizationId, work.JobKey, work.JobVersion, work.State, work.Attempts,
                work.OperatorPaused, work.RecoveryGeneration, work.CreatedAtUtc, work.DueAtUtc,
                work.NextAttemptAtUtc, work.LastFailureCode
            }).ToArrayAsync(cancellationToken);
        ModuleWorkView[] items = rows.Select(work => new ModuleWorkView(work.Id, work.OrganizationId, work.JobKey,
            work.JobVersion, work.State, work.Attempts, work.OperatorPaused, work.RecoveryGeneration,
            work.CreatedAtUtc, work.DueAtUtc, work.NextAttemptAtUtc, SafeFailureCode(work.LastFailureCode))).ToArray();
        return Result.Success(new PagedResult<ModuleWorkView>(items, boundedPage, boundedSize, count));
    }

    public async Task<Result<ModuleWorkActionReceipt>> ChangeAsync(Guid actorId, Guid workId, string action,
        ModuleWorkActionRequest request, CancellationToken cancellationToken = default)
    {
        EffectivePlatformAccess access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        if (!access.HasPermission(PlatformPermissions.JobsManage))
            return Result.Failure<ModuleWorkActionReceipt>("forbidden", "Current platform access cannot control module work.");
        if (request.RequestId == Guid.Empty || workId == Guid.Empty || request.ExpectedGeneration < 0
            || action is not ("pause" or "resume" or "retry"))
            return Result.Failure<ModuleWorkActionReceipt>("invalid_request", "Work control is invalid.");
        await using ModuleWorkDbContext db = CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        string requestLock = "module-work-control/" + request.RequestId.ToString("N");
        _ = await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({requestLock}, 0))", cancellationToken);
        access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        if (!access.HasPermission(PlatformPermissions.JobsManage))
            return Result.Failure<ModuleWorkActionReceipt>("forbidden", "Current platform access cannot control module work.");
        ModuleWorkItem? work = await db.ModuleWorkItems.FromSqlInterpolated($"""
            SELECT * FROM infrastructure.module_work_items WHERE "Id"={workId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (work is null) return Result.Failure<ModuleWorkActionReceipt>("not_found", "Work was not found.");
        ModuleWorkControl? prior = await db.Set<ModuleWorkControl>().AsNoTracking()
            .SingleOrDefaultAsync(control => control.RequestId == request.RequestId, cancellationToken);
        if (prior is not null)
            return prior.WorkId == workId && prior.ActorId == actorId && prior.Action == action
                && prior.ExpectedGeneration == request.ExpectedGeneration
                ? Result.Success(new ModuleWorkActionReceipt(workId, action, prior.ResultGeneration, prior.OperatorPaused))
                : Result.Failure<ModuleWorkActionReceipt>("request_conflict", "Request identity is already in use.");
        Result<ModuleWorkActionReceipt> outcome = ApplyControl(work, action, request.ExpectedGeneration);
        if (!outcome.IsSuccess) return outcome;
        db.Set<ModuleWorkControl>().Add(new()
        {
            RequestId = request.RequestId, WorkId = workId, ActorId = actorId, Action = action,
            ExpectedGeneration = request.ExpectedGeneration, ResultGeneration = work.RecoveryGeneration,
            OperatorPaused = work.OperatorPaused, Outcome = "accepted", OccurredAtUtc = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    internal static Result<ModuleWorkActionReceipt> ApplyControl(ModuleWorkItem work, string action, int expectedGeneration)
    {
        if (work.RecoveryGeneration != expectedGeneration)
            return Result.Failure<ModuleWorkActionReceipt>("request_conflict", "Work changed; reload before applying this control.");
        if (expectedGeneration == int.MaxValue || work.State is "leased" or "completed"
            || action == "pause" && (work.OperatorPaused || work.State is not ("queued" or "paused"))
            || action == "resume" && !work.OperatorPaused
            || action == "retry" && work.State is not ("terminal" or "denied")
            || action is not ("pause" or "resume" or "retry"))
            return Result.Failure<ModuleWorkActionReceipt>("state_conflict", "This control is not available for the current work state.");
        switch (action)
        {
            case "pause": work.OperatorPaused = true; break;
            case "resume": work.OperatorPaused = false; break;
            case "retry":
                work.State = "queued";
                work.Attempts = 0;
                work.NextAttemptAtUtc = null;
                work.LeaseId = null;
                work.LeaseUntilUtc = null;
                work.LastFailureCode = null;
                break;
        }
        work.RecoveryGeneration++;
        return Result.Success(new ModuleWorkActionReceipt(work.Id, action, work.RecoveryGeneration, work.OperatorPaused));
    }

    internal static string? SafeFailureCode(string? value) => value is null ? null
        : FailureCodes.Contains(value) ? value : "unclassified";

    private ModuleWorkDbContext CreateContext() => new(new DbContextOptionsBuilder<ModuleWorkDbContext>()
        .UseNpgsql(RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator)).Options);
}
