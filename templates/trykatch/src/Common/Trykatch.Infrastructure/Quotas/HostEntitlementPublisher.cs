using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>One complete, independent platform transaction before any parent quota lock.</summary>
internal sealed class HostEntitlementPublisher(QuotaPublicationAuthority authority, IConfiguration configuration,
    ModuleCatalog catalog, TimeProvider timeProvider) : IOrganizationEntitlementProjectionPublisher
{
    internal const string Source = "host-configuration";
    internal static readonly Guid AuditActorId = new("6906d594-2851-4d7b-9ff4-3e2416d270a1");
    private readonly string platformConnection = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Platform);

    public async Task PublishAsync(EntitlementPublication publication, CancellationToken cancellationToken = default)
    {
        QuotaPublicationAuthority.Frame frame = authority.Validate(publication);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = timeout.Token;
        try
        {
            if (new NpgsqlConnectionStringBuilder(platformConnection).Username != "trykatch_platform_runtime"
                || frame.Transaction is not NpgsqlTransaction parent || parent.Connection is not { } parentConnection)
                throw new OrganizationQuotaUnavailableException();
            await ValidateParentScopeAsync(parentConnection, parent, frame, token);
            await using NpgsqlConnection connection = new(platformConnection);
            await connection.OpenAsync(token);
            // Advisory locks and app projections must share the actual supported PostgreSQL target.
            if (connection.Host != parentConnection.Host || connection.Port != parentConnection.Port
                || connection.Database != parentConnection.Database)
                throw new OrganizationQuotaUnavailableException();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            if (await ServerIdentityAsync(connection, transaction, token) != await ServerIdentityAsync(parentConnection, parent, token))
                throw new OrganizationQuotaUnavailableException();
            await using (NpgsqlCommand scope = Command(connection, transaction, """
                SELECT set_config('app.organization_id', @organization_text, true),
                    set_config('app.actor_id', @actor_text, true), set_config('statement_timeout', '5000', true),
                    set_config('lock_timeout', '1000', true)
                """, frame.OrganizationId))
            {
                scope.Parameters.AddWithValue("organization_text", frame.OrganizationId.ToString());
                scope.Parameters.AddWithValue("actor_text", AuditActorId.ToString());
                await scope.ExecuteNonQueryAsync(token);
            }
            await using (NpgsqlCommand target = Command(connection, transaction, """
                SELECT current_user = 'trykatch_platform_runtime' AND EXISTS (
                    SELECT 1 FROM platform.organizations o
                    JOIN platform.organization_data_placements p ON p."OrganizationId" = o."Id"
                    WHERE o."Id" = @organization AND o."IsActive" AND p."Placement" = 1 AND p."State" = 2
                        AND p."Provider" = 'postgres' AND p."DatabaseIdentifier" = current_database())
                """, frame.OrganizationId))
                if (await target.ExecuteScalarAsync(token) is not true) throw new OrganizationQuotaUnavailableException();
            await TakeLockAsync(connection, transaction, frame.OrganizationId,
                QuotaResourceLock.PublicationKey(frame.OrganizationId), token);
            QuotaPublicationReceipt receipt = await PublishBatchAsync(connection, transaction, frame, token);
            authority.Validate(frame);
            await transaction.CommitAsync(token);
            authority.Complete(frame, receipt);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
        catch (Exception failure) when (failure is DbException or TimeoutException
            or InvalidOperationException { InnerException: DbException })
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
    }

    private async Task<QuotaPublicationReceipt> PublishBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        QuotaPublicationAuthority.Frame frame, CancellationToken token)
    {
        QuotaSettings settings = frame.Settings;
        QuotaPublicationReceipt? latest = null;
        await using (NpgsqlCommand header = Command(connection, transaction, """
            SELECT "SourceRevision", "SourceHash" FROM platform.entitlement_publication_history
            WHERE "OrganizationId" = @organization ORDER BY "SourceRevision" DESC LIMIT 1
            """, frame.OrganizationId))
        await using (NpgsqlDataReader reader = await header.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token)) latest = new(reader.GetInt64(0), reader.GetString(1));

        Dictionary<string, Projection> retained = new(StringComparer.Ordinal);
        await using (NpgsqlCommand rows = Command(connection, transaction, """
            SELECT "ResourceKey", "OwnerModuleId", "Unit", "Source", "SourceRevision", "SourceHash", "DisplayName"
            FROM app.organization_entitlements WHERE "OrganizationId" = @organization ORDER BY "ResourceKey" LIMIT 10001
            """, frame.OrganizationId))
        await using (NpgsqlDataReader reader = await rows.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                retained.Add(reader.GetString(0), new(reader.GetString(1), (ModuleQuotaUnit)reader.GetInt32(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        if (retained.Count > 10_000 || latest is null && retained.Count != 0
            || latest is not null && retained.Values.Any(row => row.Source != Source || row.Revision != latest.Revision || row.Hash != latest.Hash))
            throw new OrganizationQuotaUnavailableException();
        Dictionary<string, Resource> current = catalog.Descriptors.SelectMany(module => module.QuotaResources
            .Select(resource => new KeyValuePair<string, Resource>(resource.Key, new(module.Id, resource.Unit, resource.Name))))
            .ToDictionary(StringComparer.Ordinal);
        foreach ((string key, Resource resource) in current)
            if (retained.TryGetValue(key, out Projection? row) && (row.Owner != resource.Owner || row.Unit != resource.Unit))
                throw new OrganizationQuotaUnavailableException();

        if (latest is not null && latest.Revision > settings.Revision) return latest;
        if (latest is not null && latest.Revision == settings.Revision && latest.Hash != settings.Hash)
            throw new OrganizationQuotaUnavailableException();
        bool newRevision = latest is null || latest.Revision < settings.Revision;
        Dictionary<string, Resource> batch = newRevision
            ? retained.ToDictionary(pair => pair.Key, pair => new Resource(pair.Value.Owner, pair.Value.Unit, pair.Value.Name), StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        foreach ((string key, Resource resource) in current)
            if (newRevision || !retained.ContainsKey(key)) batch[key] = resource;
        if (batch.Count > 10_000) throw new OrganizationQuotaUnavailableException();
        DateTimeOffset now = new(timeProvider.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
        Dictionary<string, Resource> displayUpdates = current.Where(pair => retained.TryGetValue(pair.Key, out Projection? row)
                && row.Name != pair.Value.Name && !batch.ContainsKey(pair.Key)).ToDictionary(StringComparer.Ordinal);
        foreach ((string key, Resource resource) in batch.Concat(displayUpdates).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await TakeLockAsync(connection, transaction, frame.OrganizationId,
                QuotaResourceLock.ResourceKey(frame.OrganizationId, key), token);
            if (displayUpdates.ContainsKey(key))
            {
                // Refresh cosmetic catalog metadata without rewriting the immutable allowance version or history.
                await using NpgsqlCommand display = Command(connection, transaction, """
                    UPDATE app.organization_entitlements SET "DisplayName" = @name
                    WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource
                    """, frame.OrganizationId);
                display.Parameters.AddWithValue("resource", key);
                display.Parameters.AddWithValue("name", resource.Name!);
                await display.ExecuteNonQueryAsync(token);
                continue;
            }
            QuotaAllowance allowance = !settings.EnforcementEnabled ? new(OrganizationEntitlementMode.Disabled, null)
                : settings.Allowances.GetValueOrDefault(key) ?? new(OrganizationEntitlementMode.Unconfigured, null);
            await using NpgsqlCommand write = Command(connection, transaction, """
                INSERT INTO app.organization_entitlements
                    ("OrganizationId", "ResourceKey", "OwnerModuleId", "DisplayName", "Unit", "Mode", "Limit", "Version",
                     "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
                VALUES (@organization, @resource, @owner, @name, @unit, @mode, @limit, @version, @source, @revision, @hash, @now)
                ON CONFLICT ("OrganizationId", "ResourceKey") DO UPDATE SET
                    "DisplayName" = EXCLUDED."DisplayName", "Mode" = EXCLUDED."Mode", "Limit" = EXCLUDED."Limit", "Version" = EXCLUDED."Version",
                    "Source" = EXCLUDED."Source", "SourceRevision" = EXCLUDED."SourceRevision",
                    "SourceHash" = EXCLUDED."SourceHash", "EffectiveAtUtc" = EXCLUDED."EffectiveAtUtc"
                """, frame.OrganizationId);
            write.Parameters.AddWithValue("resource", key);
            write.Parameters.AddWithValue("owner", resource.Owner);
            write.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, (object?)resource.Name ?? DBNull.Value);
            write.Parameters.AddWithValue("unit", (int)resource.Unit);
            write.Parameters.AddWithValue("mode", (int)allowance.Mode);
            write.Parameters.AddWithValue("limit", NpgsqlDbType.Bigint, (object?)allowance.Limit ?? DBNull.Value);
            write.Parameters.AddWithValue("version", Guid.CreateVersion7());
            write.Parameters.AddWithValue("source", Source);
            write.Parameters.AddWithValue("revision", settings.Revision);
            write.Parameters.AddWithValue("hash", settings.Hash);
            write.Parameters.AddWithValue("now", now);
            await write.ExecuteNonQueryAsync(token);
        }
        if (newRevision)
        {
            await using NpgsqlCommand history = Command(connection, transaction, """
                INSERT INTO platform.entitlement_publication_history
                    ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
                VALUES (@organization, @revision, @hash, @source, 1, @actor, @count, @now)
                """, frame.OrganizationId);
            history.Parameters.AddWithValue("revision", settings.Revision);
            history.Parameters.AddWithValue("hash", settings.Hash);
            history.Parameters.AddWithValue("source", Source);
            history.Parameters.AddWithValue("actor", AuditActorId);
            history.Parameters.AddWithValue("count", batch.Count);
            history.Parameters.AddWithValue("now", now);
            await history.ExecuteNonQueryAsync(token);
        }
        return new(settings.Revision, settings.Hash);
    }

    private static async Task ValidateParentScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        QuotaPublicationAuthority.Frame frame, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection, transaction, """
            SELECT current_user = 'trykatch_org_runtime'
                AND NULLIF(current_setting('app.organization_id', true), '')::uuid = @organization
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid = @actor
            """, frame.OrganizationId);
        command.Parameters.AddWithValue("actor", frame.ActorId);
        if (await command.ExecuteScalarAsync(token) is not true)
            throw new InvalidOperationException("Quota publication authority must match the parent organization and actor scope.");
    }

    private static async Task<string> ServerIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using NpgsqlCommand command = new("""
            SELECT current_database() || '/' || COALESCE(inet_server_addr()::text, 'local') || '/'
                || COALESCE(inet_server_port()::text, 'local') || '/' || pg_postmaster_start_time()::text
            """, connection, transaction) { CommandTimeout = 5 };
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task TakeLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organizationId, long key, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection, transaction, "SELECT pg_try_advisory_xact_lock(@key)", organizationId);
        command.Parameters.AddWithValue("key", key);
        if (await command.ExecuteScalarAsync(token) is not true) throw new OrganizationQuotaUnavailableException();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid organizationId)
    {
        NpgsqlCommand command = new(sql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("organization", organizationId);
        return command;
    }

    private sealed record Projection(string Owner, ModuleQuotaUnit Unit, string Source, long Revision, string Hash, string? Name);
    private sealed record Resource(string Owner, ModuleQuotaUnit Unit, string? Name);
}
