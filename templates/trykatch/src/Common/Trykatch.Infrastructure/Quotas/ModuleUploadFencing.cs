using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>Closed host lifecycle; callers must establish receipt or leased recovery-reference authority.</summary>
internal sealed class ModuleUploadFencing(QuotaMaintenanceScopeFactory scopes, IConditionalObjectStorage storage, TimeProvider clock)
{
    internal async Task<ModuleUploadCancelResult> CancelAdmittedAsync(ModuleUploadOperationRecord expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string owner = expected.ResourceKey.Split('.')[0];
        if (expected.UploadPolicyKey is null || expected.UploadPolicyVersion is not > 0
            || !expected.UploadPolicyKey.StartsWith(owner + ".", StringComparison.Ordinal)
            || expected.ObjectKey != ModuleUploadAdmission.ObjectKey(expected.OrganizationId, owner, expected.OperationId, expected.SubjectId)
            || !ConditionalObjectPolicy.TryIdentity(ModuleUploadAdmission.Identity(expected), out _)
            || expected.StorageNamespaceFingerprint != storage.NamespaceFingerprint)
            return Unavailable("upload_identity_unavailable");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            CancellationToken token = timeout.Token;
            ModuleUploadCancelResult? terminal = await CommitFenceAsync(expected, token);
            if (terminal is not null) return terminal;
            // The database scope and its advisory lock have ended before storage I/O starts.
            if (!await ConfirmSentinelAsync(expected, token)) return Unavailable("upload_fence_unconfirmed");
            return await ReleaseFencedHoldAsync(expected, token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("upload_fence_unavailable");
        }
        catch (Exception failure) when (failure is OrganizationQuotaUnavailableException or DbException or IOException
            or TimeoutException or InvalidOperationException { InnerException: DbException })
        {
            return Unavailable("upload_fence_unavailable");
        }
    }

    // Null means the fence has committed and storage work may now begin.
    private async Task<ModuleUploadCancelResult?> CommitFenceAsync(ModuleUploadOperationRecord expected, CancellationToken token)
    {
        await using QuotaMaintenanceScope? scope = await scopes.TryCreateAsync(expected.OrganizationId, token);
        if (scope is null) return Unavailable("quota_target_ineligible");
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        if (!await QuotaResourceLock.TryAcquireAsync(application, expected.OrganizationId, expected.ResourceKey, token))
            return Unavailable("quota_busy");
        await scope.ValidateAsync(token);
        ModuleUploadOperationRecord? operation = await ReadValidAdmissionAsync(application, expected, token);
        if (operation is null) return Unavailable("upload_intent_unavailable");
        if (operation.State == ModuleUploadState.Committed)
            return new(ModuleUploadCancelDisposition.Conflict, "upload_committed");
        if (operation.State == ModuleUploadState.Cancelled)
            return new(ModuleUploadCancelDisposition.AlreadyCancelled);
        if (operation.State == ModuleUploadState.Admitted)
        {
            int changed = await application.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.module_upload_operations SET "State" = 2
                WHERE "OrganizationId" = {expected.OrganizationId} AND "ResourceKey" = {expected.ResourceKey}
                    AND "OperationId" = {expected.OperationId} AND "State" = 0
                """, token);
            if (changed != 1) return Unavailable("upload_intent_unavailable");
        }
        else if (operation.State != ModuleUploadState.Fencing) return Unavailable("upload_intent_unavailable");
        await scope.CommitAsync(token);
        return null;
    }

    private async Task<bool> ConfirmSentinelAsync(ModuleUploadOperationRecord operation, CancellationToken token)
    {
        ObjectWriteIdentity identity = ModuleUploadAdmission.Identity(operation);
        ConditionalObjectStat before = await storage.StatAsync(operation.ObjectKey, token);
        if (IsSentinel(before, identity)) return true;
        if (before.State != ConditionalObjectState.Missing
            && (before.State != ConditionalObjectState.Provisional || before.Length != operation.Length
                || before.Revision is null || !ConditionalObjectPolicy.SameIdentity(before.Identity, identity))) return false;
        _ = await storage.TryReplaceWithCancellationAsync(operation.ObjectKey, before.Revision, identity, token);
        // Unknown write outcomes never release capacity without an independent current observation.
        return IsSentinel(await storage.StatAsync(operation.ObjectKey, token), identity);
    }

    private async Task<ModuleUploadCancelResult> ReleaseFencedHoldAsync(ModuleUploadOperationRecord expected, CancellationToken token)
    {
        await using QuotaMaintenanceScope? scope = await scopes.TryCreateAsync(expected.OrganizationId, token);
        if (scope is null) return Unavailable("quota_target_ineligible");
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        if (!await QuotaResourceLock.TryAcquireAsync(application, expected.OrganizationId, expected.ResourceKey, token))
            return Unavailable("quota_busy");
        await scope.ValidateAsync(token);
        ModuleUploadOperationRecord? operation = await ReadValidAdmissionAsync(application, expected, token);
        if (operation is null) return Unavailable("upload_intent_unavailable");
        if (operation.State == ModuleUploadState.Cancelled) return new(ModuleUploadCancelDisposition.AlreadyCancelled);
        if (operation.State != ModuleUploadState.Fencing) return Unavailable("upload_intent_unavailable");
        DateTimeOffset now = clock.GetUtcNow();
        if (now < operation.CreatedAtUtc) now = operation.CreatedAtUtc;
        int released = await application.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.organization_quota_reservations SET "State" = 2, "ClosedAtUtc" = {now}
            WHERE "OrganizationId" = {operation.OrganizationId} AND "Id" = {operation.ReservationId}
                AND "State" = 0 AND "EffectKind" = 2
            """, token);
        int cancelled = await application.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE app.module_upload_operations SET "State" = 3, "ClosedAtUtc" = {now}
            WHERE "OrganizationId" = {operation.OrganizationId} AND "ResourceKey" = {operation.ResourceKey}
                AND "OperationId" = {operation.OperationId} AND "State" = 2
            """, token);
        if (released != 1 || cancelled != 1) throw new OrganizationQuotaUnavailableException();
        await scope.CommitAsync(token);
        return new(ModuleUploadCancelDisposition.Cancelled);
    }

    private static async Task<ModuleUploadOperationRecord?> ReadValidAdmissionAsync(ApplicationDbContext application,
        ModuleUploadOperationRecord expected, CancellationToken token)
    {
        ModuleUploadOperationRecord? operation = await application.Set<ModuleUploadOperationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == expected.OrganizationId && row.ResourceKey == expected.ResourceKey
                && row.OperationId == expected.OperationId, token);
        if (operation is null || !ModuleUploadAdmission.SameAdmission(operation, expected)) return null;
        OrganizationQuotaReservationRecord? hold = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == expected.OrganizationId && row.Id == expected.ReservationId, token);
        return hold is not null && ModuleUploadAdmission.MatchesHold(operation, hold) ? operation : null;
    }

    private static bool IsSentinel(ConditionalObjectStat stored, ObjectWriteIdentity identity) =>
        stored.State == ConditionalObjectState.Cancellation && stored.Length == 0 && stored.Revision is not null
        && ConditionalObjectPolicy.SameIdentity(stored.Identity, identity);
    private static ModuleUploadCancelResult Unavailable(string code) => new(ModuleUploadCancelDisposition.Unavailable, code);
}
