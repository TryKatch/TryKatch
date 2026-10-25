using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>Closed host reconciliation. Durable references never select a module handler or confer user authority.</summary>
internal sealed class QuotaMaintenanceExecutor(QuotaMaintenanceScopeFactory scopes,
    ModuleUploadFencing fencing, QuotaInventoryReconciler inventory)
{
    internal async Task<QuotaMaintenanceResult> ExecuteAsync(QuotaMaintenanceWorkRecord work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (work.OrganizationId == Guid.Empty || work.Id == Guid.Empty || work.State != QuotaMaintenanceState.Leased
            || work.LeaseId is null || work.LeaseId == Guid.Empty)
            return Unavailable("quota_maintenance_reference_invalid");
        return work.Kind switch
        {
            QuotaMaintenanceKind.TransactionOnlyExpiry when work.ReservationId is { } reservation && reservation != Guid.Empty
                && work.UploadOperationId is null => await ExpireAsync(work, cancellationToken),
            QuotaMaintenanceKind.UploadFencing when work.ReservationId is { } hold && hold != Guid.Empty
                && work.UploadOperationId is { } operation && operation != Guid.Empty => await FenceAsync(work, cancellationToken),
            QuotaMaintenanceKind.Inventory when work.ReservationId is null && work.UploadOperationId is null =>
                MapInventory(await inventory.ReconcilePageAsync(work.OrganizationId, work.ResourceKey, cancellationToken)),
            _ => Unavailable("quota_maintenance_reference_invalid")
        };
    }

    private async Task<QuotaMaintenanceResult> ExpireAsync(QuotaMaintenanceWorkRecord work, CancellationToken token)
    {
        await using QuotaMaintenanceScope? scope = await scopes.TryCreateAsync(work.OrganizationId, token);
        if (scope is null) return Paused();
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        if (!await QuotaResourceLock.TryAcquireAsync(application, work.OrganizationId, work.ResourceKey, token))
            return Unavailable("quota_busy");
        await scope.ValidateAsync(token);
        OrganizationQuotaReservationRecord? hold = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.Id == work.ReservationId
                && row.ResourceKey == work.ResourceKey, token);
        if (hold is null || hold.EffectKind != QuotaReservationEffectKind.TransactionOnly
            || await application.Set<ModuleUploadOperationRecord>().AsNoTracking().AnyAsync(row =>
                row.OrganizationId == work.OrganizationId && row.ReservationId == hold.Id, token))
            return Unavailable("quota_maintenance_reference_invalid");
        if (hold.State is ModuleQuotaReservationState.Committed or ModuleQuotaReservationState.Released) return Completed();
        DateTimeOffset now = await DatabaseNowAsync(application, token);
        if (hold.ExpiresAtUtc > now) return new(QuotaMaintenanceExecutionState.InProgress, "quota_reservation_not_expired");
        int released = await application.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.organization_quota_reservations SET "State" = 2, "ClosedAtUtc" = {now}
            WHERE "OrganizationId" = {work.OrganizationId} AND "Id" = {hold.Id} AND "ResourceKey" = {work.ResourceKey}
                AND "EffectKind" = 1 AND "State" = 0 AND "ExpiresAtUtc" <= {now}
            """, token);
        if (released != 1) return Unavailable("quota_maintenance_reference_invalid");
        await scope.CommitAsync(token);
        return Completed();
    }

    private async Task<QuotaMaintenanceResult> FenceAsync(QuotaMaintenanceWorkRecord work, CancellationToken token)
    {
        ModuleUploadOperationRecord operation;
        await using (QuotaMaintenanceScope? scope = await scopes.TryCreateAsync(work.OrganizationId, token))
        {
            if (scope is null) return Paused();
            ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
            if (!await QuotaResourceLock.TryAcquireAsync(application, work.OrganizationId, work.ResourceKey, token))
                return Unavailable("quota_busy");
            await scope.ValidateAsync(token);
            ModuleUploadOperationRecord? stored = await application.Set<ModuleUploadOperationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.ResourceKey == work.ResourceKey
                    && row.OperationId == work.UploadOperationId, token);
            OrganizationQuotaReservationRecord? hold = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == work.OrganizationId && row.Id == work.ReservationId, token);
            if (stored is null || hold is null || stored.ReservationId != work.ReservationId
                || !ModuleUploadAdmission.MatchesHold(stored, hold))
                return Unavailable("quota_maintenance_reference_invalid");
            if (stored.State is ModuleUploadState.Committed or ModuleUploadState.Cancelled) return Completed();
            if (stored.State == ModuleUploadState.Admitted && stored.ExpiresAtUtc > await DatabaseNowAsync(application, token))
                return new(QuotaMaintenanceExecutionState.InProgress, "quota_upload_not_expired");
            operation = stored;
        }
        // End the validating transaction/resource lock before the fencing core creates independent scopes and performs I/O.
        ModuleUploadCancelResult result = await fencing.CancelAdmittedAsync(operation, token);
        return result.Disposition switch
        {
            ModuleUploadCancelDisposition.Cancelled or ModuleUploadCancelDisposition.AlreadyCancelled => Completed(),
            ModuleUploadCancelDisposition.Conflict when result.FailureCode == "upload_committed" => Completed(),
            _ when result.FailureCode == "quota_target_ineligible" => Paused(),
            _ => Unavailable(result.FailureCode ?? "quota_maintenance_unavailable")
        };
    }

    private static Task<DateTimeOffset> DatabaseNowAsync(ApplicationDbContext application, CancellationToken token) =>
        application.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);

    private static QuotaMaintenanceResult MapInventory(QuotaInventoryReconciliationResult result) => new(result.State switch
    {
        QuotaInventoryReconciliationState.Complete => QuotaMaintenanceExecutionState.Completed,
        QuotaInventoryReconciliationState.InProgress => QuotaMaintenanceExecutionState.InProgress,
        QuotaInventoryReconciliationState.Paused => QuotaMaintenanceExecutionState.Paused,
        _ => QuotaMaintenanceExecutionState.Unavailable
    }, result.FailureCode);

    private static QuotaMaintenanceResult Paused() => new(QuotaMaintenanceExecutionState.Paused, "quota_target_ineligible");
    private static QuotaMaintenanceResult Completed() => new(QuotaMaintenanceExecutionState.Completed);
    private static QuotaMaintenanceResult Unavailable(string code) => new(QuotaMaintenanceExecutionState.Unavailable, code);
}

/// <summary>Claims and acknowledges through the coordinator, executing each reference in a separate host scope.</summary>
internal sealed partial class QuotaMaintenanceProcessor(QuotaMaintenanceStore store, IServiceScopeFactory scopes,
    ILogger<QuotaMaintenanceProcessor> logger)
{
    internal async Task<QuotaMaintenanceResult> ProcessAsync(Guid workId, CancellationToken cancellationToken)
    {
        QuotaMaintenanceClaim? claim = await store.ClaimAsync(workId, cancellationToken);
        if (claim is not null) return await ExecuteClaimAsync(claim, cancellationToken);
        return await store.IsCompleteAsync(workId, cancellationToken)
            ? new(QuotaMaintenanceExecutionState.Completed)
            : new(QuotaMaintenanceExecutionState.InProgress, "quota_work_not_ready");
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        int processed = 0;
        for (; processed < 20; processed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaMaintenanceClaim? claim = await store.ClaimAsync(null, cancellationToken);
            if (claim is null) break;
            _ = await ExecuteClaimAsync(claim, cancellationToken);
        }
        return processed;
    }

    private async Task<QuotaMaintenanceResult> ExecuteClaimAsync(QuotaMaintenanceClaim claim, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        QuotaMaintenanceResult result;
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            result = await scope.ServiceProvider.GetRequiredService<QuotaMaintenanceExecutor>().ExecuteAsync(claim.Work, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(5));
            try { await store.ReleaseAsync(claim, cleanup.Token); }
            catch (Exception) { /* Its durable lease remains reclaimable if shutdown cleanup is unavailable. */ }
            throw;
        }
        catch (Exception failure)
        {
            LogFailure(logger, (int)claim.Work.Kind, failure.GetType().FullName ?? failure.GetType().Name);
            result = new(QuotaMaintenanceExecutionState.Unavailable, "quota_maintenance_unavailable");
        }
        return await store.FinishAsync(claim, result, cancellationToken)
            ? result : new(QuotaMaintenanceExecutionState.Unavailable, "quota_maintenance_lease_lost");
    }

    [LoggerMessage(EventId = 4602, Level = LogLevel.Error, Message = "Quota maintenance kind {Kind} failed with {ExceptionType}")]
    private static partial void LogFailure(ILogger logger, int kind, string exceptionType);
}
