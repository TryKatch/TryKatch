using Microsoft.EntityFrameworkCore;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class OrganizationUsageLedger(ApplicationDbContext application)
{
    internal async Task<(long Committed, long Held)> TotalsAsync(Guid organizationId, string resourceKey,
        CancellationToken cancellationToken)
    {
        long committed = await application.Database.SqlQuery<long>($"""
            SELECT COALESCE(sum("Units"), 0)::bigint AS "Value" FROM app.organization_usage
            WHERE "OrganizationId" = {organizationId} AND "ResourceKey" = {resourceKey}
            """).SingleAsync(cancellationToken);
        // Expiry is a reconciliation deadline. It never proves an outstanding hold is safe to release.
        long held = await application.Database.SqlQuery<long>($"""
            SELECT COALESCE(sum("Units"), 0)::bigint AS "Value" FROM app.organization_quota_reservations
            WHERE "OrganizationId" = {organizationId} AND "ResourceKey" = {resourceKey} AND "State" = 0
            """).SingleAsync(cancellationToken);
        return (committed, held);
    }

    internal Task<int> CommitReservationAsync(OrganizationQuotaReservationRecord reservation, DateTimeOffset now,
        CancellationToken cancellationToken) => application.Database.ExecuteSqlInterpolatedAsync($"""
            WITH closed AS (
                UPDATE app.organization_quota_reservations SET "State" = 1, "ClosedAtUtc" = {now}
                WHERE "OrganizationId" = {reservation.OrganizationId} AND "Id" = {reservation.Id}
                    AND "ResourceKey" = {reservation.ResourceKey} AND "ActorId" = {reservation.ActorId}
                    AND "State" = 0 AND "EffectKind" = 1
                RETURNING "OrganizationId", "ResourceKey", "OperationId", "Id", "ActorId", "Units")
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            SELECT "OrganizationId", "ResourceKey", "OperationId", "Id", NULL, "Id", "ActorId", "Units", {now}
            FROM closed
            """, cancellationToken);
}
