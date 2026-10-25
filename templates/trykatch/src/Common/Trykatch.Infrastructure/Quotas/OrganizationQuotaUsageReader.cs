using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class OrganizationQuotaUsageReader(
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext control,
    IOrganizationContext organization,
    IOrganizationAccessResolver accessResolver,
    IOrganizationDataPlacement placement,
    ModuleCatalog catalog,
    QuotaPublicationAuthority publication,
    IOrganizationEntitlementProjectionPublisher publisher) : IOrganizationQuotaUsageReader
{
    public async Task<Result<OrganizationQuotaUsage>> GetAsync(CancellationToken cancellationToken = default)
    {
        DbTransaction transaction = RequireTransaction();
        Guid lifetime = control.Database.CurrentTransaction!.TransactionId;
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            CancellationToken token = timeout.Token;
            if (!await HasHumanAuthorityAsync(token)) return Forbidden();
            QuotaPublicationReceipt receipt = await publication.EnsurePublishedAsync(publisher, token);
            ValidateLifetime(transaction, lifetime);
            if (!await HasHumanAuthorityAsync(token)) return Forbidden();

            // Entitlement, inventory and every aggregate share one PostgreSQL statement snapshot.
            // SUM(bigint) is numeric: read its decimal text and reject overflow without aborting the parent transaction.
            Snapshot[] snapshots = await application.Database.SqlQuery<Snapshot>($"""
                SELECT e."ResourceKey", e."OwnerModuleId", e."DisplayName", e."Unit", e."Mode", e."Limit", e."Version", e."Source",
                    e."SourceRevision", e."SourceHash", i."SourceKey", i."SourceVersion", i."State" AS "InventoryState",
                    COALESCE(u.total, 0)::text AS "Committed", COALESCE(h.reserved, 0)::text AS "Reserved",
                    COALESCE(h.reconciling, 0)::text AS "ReconciliationHeld"
                FROM app.organization_entitlements e
                LEFT JOIN app.organization_quota_inventory i
                    ON i."OrganizationId" = e."OrganizationId" AND i."ResourceKey" = e."ResourceKey"
                LEFT JOIN LATERAL (
                    SELECT sum("Units") AS total FROM app.organization_usage
                    WHERE "OrganizationId" = e."OrganizationId" AND "ResourceKey" = e."ResourceKey"
                ) u ON true
                LEFT JOIN LATERAL (
                    SELECT sum(r."Units") FILTER (WHERE r."ExpiresAtUtc" > statement_timestamp() AND NOT EXISTS (
                            SELECT 1 FROM app.module_upload_operations op WHERE op."OrganizationId" = r."OrganizationId"
                                AND op."ReservationId" = r."Id" AND op."State" = 2)) AS reserved,
                        sum(r."Units") FILTER (WHERE r."ExpiresAtUtc" <= statement_timestamp() OR EXISTS (
                            SELECT 1 FROM app.module_upload_operations op WHERE op."OrganizationId" = r."OrganizationId"
                                AND op."ReservationId" = r."Id" AND op."State" = 2)) AS reconciling
                    FROM app.organization_quota_reservations r
                    WHERE r."OrganizationId" = e."OrganizationId" AND r."ResourceKey" = e."ResourceKey" AND r."State" = 0
                ) h ON true
                WHERE e."OrganizationId" = {organization.OrganizationId}
                ORDER BY e."ResourceKey" LIMIT 10001
                """).ToArrayAsync(token);
            ValidateLifetime(transaction, lifetime);
            if (!await HasHumanAuthorityAsync(token)) return Forbidden();
            if (snapshots.Length > 10_000) return Unavailable();

            Dictionary<string, InstalledModuleQuotaResource> installed = catalog.Descriptors
                .SelectMany(module => module.QuotaResources.Select(resource => new InstalledModuleQuotaResource(module.Id, resource)))
                .ToDictionary(item => item.Resource.Key, StringComparer.Ordinal);
            Dictionary<string, Snapshot> durable = snapshots.ToDictionary(row => row.ResourceKey, StringComparer.Ordinal);
            List<OrganizationQuotaUsageResource> resources = [];
            foreach (string key in installed.Keys.Union(durable.Keys).Order(StringComparer.Ordinal))
            {
                installed.TryGetValue(key, out InstalledModuleQuotaResource? current);
                durable.TryGetValue(key, out Snapshot? row);
                bool valid = row is not null && row.Source == HostEntitlementPublisher.Source
                    && (row.SourceRevision > receipt.Revision || row.SourceRevision == receipt.Revision && row.SourceHash == receipt.Hash)
                    && (current is null || row.OwnerModuleId == current.ModuleId && row.Unit == (int)current.Resource.Unit)
                    && TotalsValid(row);
                bool sourceMatches = current?.Resource.InventorySource is not { } source
                    || row?.SourceKey == source.SourceKey && row.SourceVersion == source.SourceVersion;
                bool complete = sourceMatches && row?.InventoryState == (int)QuotaInventoryState.Complete;
                string state = !valid ? "Unavailable" : current is null ? "Paused" : complete ? "Ready"
                    : current.Resource.InventorySource is null ? "Paused"
                    : sourceMatches && row!.InventoryState == (int)QuotaInventoryState.Unavailable ? "Unavailable"
                    : sourceMatches && row!.InventoryState == (int)QuotaInventoryState.Paused ? "Paused" : "Initializing";
                resources.Add(new(key, current?.Resource.Name ?? row?.DisplayName ?? key,
                    (current?.Resource.Unit ?? (ModuleQuotaUnit?)row?.Unit)?.ToString() ?? "Count",
                    row is null ? "Unconfigured" : ((OrganizationEntitlementMode)row.Mode).ToString(),
                    row?.Limit?.ToString(CultureInfo.InvariantCulture), row?.Version, row?.Source,
                    valid && complete ? row!.Committed : null, valid ? row!.Reserved : null,
                    valid ? row!.ReconciliationHeld : null, state, current is not null));

                if (valid && !complete && current?.Resource.InventorySource is not null)
                {
                    // Closed INSERT-only intent; the worker owns inventory reads and never runs in this request.
                    await application.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO infrastructure.quota_maintenance_work
                            ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State",
                             "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
                        VALUES ({organization.OrganizationId}, {Guid.CreateVersion7()}, 3, {key}, NULL, NULL, 0,
                            statement_timestamp(), statement_timestamp(), 0, NULL, NULL, NULL)
                        ON CONFLICT DO NOTHING
                        """, token);
                }
            }
            ValidateLifetime(transaction, lifetime);
            return Result.Success(new OrganizationQuotaUsage(resources));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception failure) when (failure is OrganizationQuotaUnavailableException or DbException or TimeoutException
            or InvalidOperationException { InnerException: DbException }) { return Unavailable(); }
    }

    private async Task<bool> HasHumanAuthorityAsync(CancellationToken token)
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty) return false;
        Guid membershipId;
        try { membershipId = organization.MembershipId; }
        catch (OrganizationMembershipRequiredException) { return false; }
        if (membershipId == Guid.Empty) return false;
        bool matches = await application.Database.SqlQuery<bool>($"""
            SELECT (NULLIF(current_setting('app.organization_id', true), '')::uuid = {organization.OrganizationId}
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid = {organization.ActorId}) IS TRUE AS "Value"
            """).SingleAsync(token);
        if (!matches) throw new InvalidOperationException("Usage inspection must match the enlisted organization and actor scope.");
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(organization.OrganizationId, token); }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException { InnerException: null }) { return false; }
        if (route.OrganizationId != organization.OrganizationId || route.Placement != OrganizationDataPlacementKind.Shared
            || route.Provider != "postgres" || route.DatabaseIdentifier != application.Database.GetDbConnection().Database) return false;
        OrganizationAccess? fresh = await accessResolver.ResolveAsync(organization.ActorId, organization.OrganizationId, token);
        return fresh is not null && fresh.OrganizationId == organization.OrganizationId && fresh.ActorId == organization.ActorId
            && fresh.MembershipId == membershipId
            && (fresh.Permissions.Contains(Permissions.OrganizationsRead) || fresh.Permissions.Contains(Permissions.OrganizationsManage));
    }

    private DbTransaction RequireTransaction()
    {
        DbTransaction? transaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null || transaction.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted
            || !ReferenceEquals(transaction, control.Database.CurrentTransaction?.GetDbTransaction()))
            throw new InvalidOperationException("Usage inspection requires the same active ReadCommitted organization transaction.");
        return transaction;
    }

    private void ValidateLifetime(DbTransaction transaction, Guid lifetime)
    {
        if (!ReferenceEquals(transaction, RequireTransaction()) || lifetime != control.Database.CurrentTransaction!.TransactionId)
            throw new InvalidOperationException("Usage inspection requires its original organization transaction.");
    }

    private static bool TotalsValid(Snapshot row)
    {
        if (!long.TryParse(row.Committed, NumberStyles.None, CultureInfo.InvariantCulture, out long committed)
            || !long.TryParse(row.Reserved, NumberStyles.None, CultureInfo.InvariantCulture, out long reserved)
            || !long.TryParse(row.ReconciliationHeld, NumberStyles.None, CultureInfo.InvariantCulture, out long held)) return false;
        return (decimal)committed + reserved + held <= long.MaxValue;
    }

    private static Result<OrganizationQuotaUsage> Forbidden() => Result.Failure<OrganizationQuotaUsage>("forbidden", "Organization usage cannot be viewed by this membership.");
    private static Result<OrganizationQuotaUsage> Unavailable() => Result.Failure<OrganizationQuotaUsage>("quota_usage_unavailable", "Organization usage could not be loaded.");

    private sealed class Snapshot
    {
        public string ResourceKey { get; set; } = "";
        public string OwnerModuleId { get; set; } = "";
        public string? DisplayName { get; set; }
        public int Unit { get; set; }
        public int Mode { get; set; }
        public long? Limit { get; set; }
        public Guid Version { get; set; }
        public string Source { get; set; } = "";
        public long SourceRevision { get; set; }
        public string SourceHash { get; set; } = "";
        public string? SourceKey { get; set; }
        public int? SourceVersion { get; set; }
        public int? InventoryState { get; set; }
        public string Committed { get; set; } = "";
        public string Reserved { get; set; } = "";
        public string ReconciliationHeld { get; set; } = "";
    }
}
