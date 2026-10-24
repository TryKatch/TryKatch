using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class OrganizationUsageLedger(ApplicationDbContext application)
{
    internal async Task AppendUploadAsync(ModuleUploadOperationRecord operation, DateTimeOffset now, CancellationToken token)
    {
        OrganizationUsageRecord? existing = await application.Set<OrganizationUsageRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == operation.OrganizationId && row.ResourceKey == operation.ResourceKey
                && row.EffectId == operation.OperationId, token);
        if (existing is not null)
        {
            if (existing.SourceId != operation.SubjectId || existing.Units != operation.Length || existing.SourceEffectId is not null
                || existing.ReservationId is { } reservationId && reservationId != operation.ReservationId)
                throw new OrganizationQuotaUnavailableException();
            return;
        }
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            VALUES ({operation.OrganizationId}, {operation.ResourceKey}, {operation.OperationId}, {operation.SubjectId}, NULL,
                {operation.ReservationId}, {operation.ActorId}, {operation.Length}, {now})
            """, token);
    }

    internal async Task AppendInventoryAsync(Guid organizationId, string resourceKey,
        IReadOnlyList<ModuleQuotaInventoryEntry> entries, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Dictionary<Guid, ModuleQuotaInventoryEntry> unique = [];
        foreach (ModuleQuotaInventoryEntry entry in entries)
        {
            if (unique.TryGetValue(entry.OperationId, out ModuleQuotaInventoryEntry? prior)
                && (prior.SourceId != entry.SourceId || prior.Units != entry.Units))
                throw new OrganizationQuotaUnavailableException();
            unique[entry.OperationId] = entry;
        }
        if (unique.Count == 0) return;
        Guid[] ids = unique.Keys.ToArray();
        OrganizationUsageRecord[] existing = await application.Set<OrganizationUsageRecord>().AsNoTracking()
            .Where(row => row.OrganizationId == organizationId && row.ResourceKey == resourceKey && ids.Contains(row.EffectId))
            .ToArrayAsync(cancellationToken);
        foreach (OrganizationUsageRecord row in existing)
        {
            ModuleQuotaInventoryEntry entry = unique[row.EffectId];
            if (row.SourceId != entry.SourceId || row.Units != entry.Units || row.SourceEffectId is not null)
                throw new OrganizationQuotaUnavailableException();
            unique.Remove(row.EffectId);
        }
        if (unique.Count == 0) return;
        string payload = JsonSerializer.Serialize(unique.Values.Select(entry => new
        {
            EffectId = entry.OperationId, entry.SourceId, entry.Units
        }));
        int written = await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            SELECT {organizationId}, {resourceKey}, item."EffectId", item."SourceId", NULL, NULL, {actorId}, item."Units", {now}
            FROM jsonb_to_recordset({payload}::jsonb) AS item("EffectId" uuid, "SourceId" uuid, "Units" bigint)
            """, cancellationToken);
        if (written != unique.Count) throw new OrganizationQuotaUnavailableException();
    }

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
