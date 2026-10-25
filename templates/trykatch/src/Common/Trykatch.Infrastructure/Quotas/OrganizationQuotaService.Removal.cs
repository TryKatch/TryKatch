using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed partial class OrganizationQuotaService
{
    public async Task<OrganizationUsageRemovalResult> RecordRemovalAsync(string resourceKey, Guid sourceEffectId,
        long units, Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (units <= 0 || sourceEffectId == Guid.Empty || operationId == Guid.Empty || sourceEffectId == operationId)
            return RemovalDenied("quota_removal_invalid");
        if (!entitlements.TryGetResource(resourceKey, out string owner, out ModuleQuotaResourceDescriptor? resource)
            || !workflows.TryGetRemoval(resourceKey, out ModuleUsageRemovalPolicyBinding? binding))
            return RemovalDenied("quota_removal_policy_unavailable");

        DbTransaction parent = application.Database.CurrentTransaction!.GetDbTransaction();
        Guid lifetime = control.Database.CurrentTransaction!.TransactionId;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            CancellationToken token = timeout.Token;
            if (!await HasAuthorityAsync(owner, resourceKey, token)) return RemovalDenied("quota_authority_denied");
            await publication.EnsurePublishedAsync(publisher, token);
            Guid organizationId = organization.OrganizationId;
            if (!await QuotaResourceLock.TryAcquireAsync(application, organizationId, resourceKey, token))
                return RemovalUnavailable("quota_busy");
            publication.ResourceLockAcquired();
            if (!await HasAuthorityAsync(owner, resourceKey, token)) return RemovalDenied("quota_authority_denied");
            OrganizationEntitlementRecord? projection = await entitlements.ReadAsync(organizationId, resourceKey, token);
            if (projection is null || projection.OwnerModuleId != owner || projection.Unit != resource!.Unit)
                return RemovalUnavailable("quota_projection_invalid");

            OrganizationUsageRecord? source = await ReadUsageEffectAsync(organizationId, resourceKey, sourceEffectId, token);
            if (source is null || source.Units <= 0 || source.SourceEffectId is not null)
                return RemovalDenied("quota_removal_source_unavailable");
            OrganizationUsageRecord? existing = await ReadUsageEffectAsync(organizationId, resourceKey, operationId, token);
            if (existing is not null && !MatchesRemoval(existing, source, units, organization.ActorId))
                return RemovalConflict();

            bool confirmed;
            try
            {
                confirmed = await binding!.Adapter.ConfirmAsync(new(source.SourceId, sourceEffectId, source.Units,
                    units, operationId), token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // A module policy is a read-only confirmation port; failure never authorizes a debit.
                cancellationToken.ThrowIfCancellationRequested();
                return RemovalUnavailable("quota_removal_confirmation_unavailable");
            }
            token.ThrowIfCancellationRequested();
            RequireTransaction();
            if (!ReferenceEquals(parent, application.Database.CurrentTransaction!.GetDbTransaction())
                || lifetime != control.Database.CurrentTransaction!.TransactionId)
                throw new InvalidOperationException("Usage removal requires its original parent transaction.");
            if (!await HasAuthorityAsync(owner, resourceKey, token)) return RemovalDenied("quota_authority_denied");
            if (!confirmed) return RemovalDenied("quota_removal_unconfirmed");

            existing = await ReadUsageEffectAsync(organizationId, resourceKey, operationId, token);
            if (existing is not null)
                return MatchesRemoval(existing, source, units, organization.ActorId)
                    ? new(OrganizationUsageRemovalDisposition.AlreadyRecorded) : RemovalConflict();
            if (await application.Set<OrganizationUsageRecord>().AsNoTracking().AnyAsync(row =>
                    row.OrganizationId == organizationId && row.ResourceKey == resourceKey
                    && row.SourceEffectId == sourceEffectId
                    && (row.SourceId != source.SourceId || row.ReservationId != null || row.Units >= 0), token))
                return RemovalUnavailable("quota_usage_invalid");
            long refunded = await application.Database.SqlQuery<long>($"""
                SELECT COALESCE(sum(-"Units"), 0)::bigint AS "Value" FROM app.organization_usage
                WHERE "OrganizationId" = {organizationId} AND "ResourceKey" = {resourceKey}
                    AND "SourceEffectId" = {sourceEffectId}
                """).SingleAsync(token);
            if (refunded < 0 || refunded > source.Units) return RemovalUnavailable("quota_usage_invalid");
            if (units > source.Units - refunded) return RemovalDenied("quota_removal_exhausted");

            int written = await application.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.organization_usage
                    ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
                VALUES ({organizationId}, {resourceKey}, {operationId}, {source.SourceId}, {sourceEffectId}, NULL,
                    {organization.ActorId}, {-units}, {UtcNow()})
                """, token);
            if (written != 1) throw new OrganizationQuotaUnavailableException();
            return new(OrganizationUsageRemovalDisposition.Recorded);
        }
        catch (OrganizationQuotaUnavailableException)
        {
            return RemovalUnavailable("quota_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemovalUnavailable("quota_unavailable");
        }
        catch (Exception failure) when (IsDatabaseFailure(failure))
        {
            return RemovalUnavailable("quota_unavailable");
        }
    }

    private Task<OrganizationUsageRecord?> ReadUsageEffectAsync(Guid organizationId, string resourceKey, Guid effectId,
        CancellationToken token) => application.Set<OrganizationUsageRecord>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.OrganizationId == organizationId && row.ResourceKey == resourceKey && row.EffectId == effectId, token);

    private static bool MatchesRemoval(OrganizationUsageRecord existing, OrganizationUsageRecord source, long units, Guid actorId) =>
        existing.SourceEffectId == source.EffectId && existing.SourceId == source.SourceId && existing.Units == -units
        && existing.ActorId == actorId && existing.ReservationId is null;

    private static OrganizationUsageRemovalResult RemovalDenied(string code) => new(OrganizationUsageRemovalDisposition.Denied, code);
    private static OrganizationUsageRemovalResult RemovalUnavailable(string code) => new(OrganizationUsageRemovalDisposition.Unavailable, code);
    private static OrganizationUsageRemovalResult RemovalConflict() => new(OrganizationUsageRemovalDisposition.Conflict, "quota_removal_conflict");
}
