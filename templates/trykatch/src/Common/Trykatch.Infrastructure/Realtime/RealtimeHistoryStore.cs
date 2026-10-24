using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Realtime;

internal sealed record RealtimeHistoryPage(IReadOnlyList<RealtimeHistoryRecord> Items, long Position, bool HasMore);

internal sealed class RealtimeHistoryStore(
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext control,
    IOrganizationContext organization)
{
    private const string Columns = """
        "OrganizationId", "Sequence", "ModuleId", "EventKey", "SchemaVersion", "EventId",
        "PolicyOwnerModuleId", "PolicyKey", "PolicyVersion", "AudienceKind", "UserId",
        "ResourceKey", "ResourceId", "SourceMessageId", "CreatedAtUtc"
        """;

    public async Task<long> ReadHeadAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlTransaction transaction = await RequireScopeAsync(null, cancellationToken);
        return await ReadHeadAsync(transaction, cancellationToken);
    }

    public async Task<RealtimeHistoryPage> ReadPageAsync(long after, CancellationToken cancellationToken = default)
    {
        NpgsqlTransaction transaction = await RequireScopeAsync(true, cancellationToken);
        long head = await ReadHeadAsync(transaction, cancellationToken);
        if (after < 0 || after > head)
            throw new InvalidOperationException("Realtime history position is unavailable.");

        await using NpgsqlCommand command = Command(transaction,
            "SELECT " + Columns + """
             FROM app.realtime_history
             WHERE "OrganizationId" = @organization AND "Sequence" > @after
             ORDER BY "Sequence" LIMIT 101
            """);
        command.Parameters.AddWithValue("after", after);
        List<RealtimeHistoryRecord> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadRecord(reader));
        bool hasMore = rows.Count > 100;
        if (hasMore) rows.RemoveAt(100);
        return new(rows, rows.Count == 0 ? after : rows[^1].Sequence, hasMore);
    }

    internal async Task AppendAsync(ModuleRealtimeNotification notification, Guid sourceMessageId, CancellationToken cancellationToken)
    {
        NpgsqlTransaction transaction = await RequireScopeAsync(false, cancellationToken);
        // Serialize the duplicate check even before the first counter row exists.
        // Both this lock and the counter's row lock live until the business commit.
        await using (NpgsqlCommand acquire = Command(transaction, """
                         SELECT pg_advisory_xact_lock(hashtextextended('realtime:' || CAST(@organization AS text), 20261024))
                         """))
            await acquire.ExecuteNonQueryAsync(cancellationToken);

        await using (NpgsqlCommand duplicate = Command(transaction,
                         "SELECT " + Columns + """
                          FROM app.realtime_history
                          WHERE "OrganizationId" = @organization AND "ModuleId" = @module AND "EventId" = @event
                         """))
        {
            duplicate.Parameters.AddWithValue("module", notification.ModuleId);
            duplicate.Parameters.AddWithValue("event", notification.EventId);
            await using NpgsqlDataReader reader = await duplicate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                RealtimeHistoryRecord previous = ReadRecord(reader);
                if (previous.SourceMessageId != sourceMessageId || previous.ToNotification() != notification)
                    throw new InvalidOperationException("Realtime event identity conflicts with committed history.");
                return;
            }
        }

        long sequence;
        await using (NpgsqlCommand allocate = Command(transaction, """
                         INSERT INTO app.realtime_streams ("OrganizationId", "Sequence") VALUES (@organization, 1)
                         ON CONFLICT ("OrganizationId") DO UPDATE SET "Sequence" = realtime_streams."Sequence" + 1
                         RETURNING "Sequence"
                         """))
            sequence = (long)(await allocate.ExecuteScalarAsync(cancellationToken))!;

        await using NpgsqlCommand append = Command(transaction, """
            INSERT INTO app.realtime_history ("OrganizationId", "Sequence", "ModuleId", "EventKey", "SchemaVersion", "EventId",
                "PolicyOwnerModuleId", "PolicyKey", "PolicyVersion", "AudienceKind", "UserId", "ResourceKey", "ResourceId",
                "SourceMessageId", "CreatedAtUtc")
            VALUES (@organization, @sequence, @module, @eventKey, @schema, @event, @policyOwner, @policyKey, @policyVersion,
                @kind, @user, @resourceKey, @resource, @source, CURRENT_TIMESTAMP)
            """);
        append.Parameters.AddWithValue("sequence", sequence);
        append.Parameters.AddWithValue("module", notification.ModuleId);
        append.Parameters.AddWithValue("eventKey", notification.EventKey);
        append.Parameters.AddWithValue("schema", notification.SchemaVersion);
        append.Parameters.AddWithValue("event", notification.EventId);
        append.Parameters.AddWithValue("policyOwner", notification.Audience.Policy.OwnerModuleId);
        append.Parameters.AddWithValue("policyKey", notification.Audience.Policy.PolicyKey);
        append.Parameters.AddWithValue("policyVersion", notification.Audience.Policy.PolicyVersion);
        append.Parameters.AddWithValue("kind", (int)notification.Audience.Kind);
        append.Parameters.AddWithValue("user", NpgsqlDbType.Uuid, (object?)notification.Audience.UserId ?? DBNull.Value);
        append.Parameters.AddWithValue("resourceKey", NpgsqlDbType.Varchar, (object?)notification.Audience.Resource?.ResourceKey ?? DBNull.Value);
        append.Parameters.AddWithValue("resource", NpgsqlDbType.Uuid, (object?)notification.Audience.Resource?.ResourceId ?? DBNull.Value);
        append.Parameters.AddWithValue("source", sourceMessageId);
        if (await append.ExecuteNonQueryAsync(cancellationToken) != 1) throw InvalidScope();
    }

    private async Task<long> ReadHeadAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(transaction,
            "SELECT \"Sequence\" FROM app.realtime_streams WHERE \"OrganizationId\" = @organization");
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private NpgsqlCommand Command(NpgsqlTransaction transaction, string sql)
    {
        NpgsqlCommand command = new(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("organization", organization.OrganizationId);
        return command;
    }

    private async Task<NpgsqlTransaction> RequireScopeAsync(bool? readOnly, CancellationToken cancellationToken)
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty)
            throw InvalidScope();
        DbTransaction? owner = control.Database.CurrentTransaction?.GetDbTransaction();
        DbTransaction? enlisted = application.Database.CurrentTransaction?.GetDbTransaction();
        if (owner is not NpgsqlTransaction transaction || !ReferenceEquals(owner, enlisted)
            || !ReferenceEquals(control.Database.GetDbConnection(), application.Database.GetDbConnection()))
            throw InvalidScope();
        await using NpgsqlCommand command = Command(transaction, """
            SELECT current_setting('transaction_read_only') = 'on',
                   current_setting('app.organization_id', true), current_setting('app.actor_id', true)
            """);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1) || reader.IsDBNull(2)
            || !Guid.TryParse(reader.GetString(1), out Guid actualOrganization)
            || !Guid.TryParse(reader.GetString(2), out Guid actualActor)
            || actualOrganization != organization.OrganizationId || actualActor != organization.ActorId
            || readOnly.HasValue && reader.GetBoolean(0) != readOnly.Value
            || readOnly == true && transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw InvalidScope();
        return transaction;
    }

    private static RealtimeHistoryRecord ReadRecord(NpgsqlDataReader reader) => new()
    {
        OrganizationId = reader.GetGuid(0), Sequence = reader.GetInt64(1), ModuleId = reader.GetString(2),
        EventKey = reader.GetString(3), SchemaVersion = reader.GetInt32(4), EventId = reader.GetGuid(5),
        PolicyOwnerModuleId = reader.GetString(6), PolicyKey = reader.GetString(7), PolicyVersion = reader.GetInt32(8),
        AudienceKind = (ModuleRealtimeAudienceKind)reader.GetInt32(9), UserId = reader.IsDBNull(10) ? null : reader.GetGuid(10),
        ResourceKey = reader.IsDBNull(11) ? null : reader.GetString(11), ResourceId = reader.IsDBNull(12) ? null : reader.GetGuid(12),
        SourceMessageId = reader.GetGuid(13), CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(14)
    };

    private static InvalidOperationException InvalidScope() =>
        new("Realtime history requires the current actor-scoped shared organization transaction.");
}
