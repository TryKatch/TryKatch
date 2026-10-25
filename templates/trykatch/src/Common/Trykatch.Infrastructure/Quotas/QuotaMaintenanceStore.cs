using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Quotas;

internal enum QuotaMaintenanceExecutionState { Completed, InProgress, Paused, Unavailable }
internal sealed record QuotaMaintenanceResult(QuotaMaintenanceExecutionState State, string? FailureCode = null);
internal sealed record QuotaMaintenanceClaim(QuotaMaintenanceWorkRecord Work, Guid LeaseId);

/// <summary>Coordinator-only leasing of closed references; never reads organization business or usage tables.</summary>
internal sealed class QuotaMaintenanceStore(IConfiguration configuration)
{
    private readonly string connectionString = RuntimeDatabaseConnectionContract.Get(configuration,
        RuntimeDatabaseConnectionContract.WorkCoordinator);

    internal async Task<QuotaMaintenanceClaim?> ClaimAsync(Guid? workId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = Deadline(cancellationToken);
        CancellationToken token = timeout.Token;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await BoundTransactionAsync(connection, transaction, token);
        Guid leaseId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new("""
            WITH due AS (
                SELECT "OrganizationId", "Id" FROM infrastructure.quota_maintenance_work
                WHERE (@id IS NULL OR "Id" = @id)
                    AND "DueAtUtc" <= statement_timestamp()
                    AND ("State" IN (0, 2) OR ("State" = 1 AND "LeaseUntilUtc" <= statement_timestamp()))
                ORDER BY "DueAtUtc", "CreatedAtUtc", "OrganizationId", "Id"
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE infrastructure.quota_maintenance_work AS work
            SET "State" = 1, "LeaseId" = @lease, "LeaseUntilUtc" = statement_timestamp() + interval '2 minutes',
                "Attempts" = CASE WHEN work."Attempts" = 2147483647 THEN work."Attempts" ELSE work."Attempts" + 1 END
            FROM due WHERE work."OrganizationId" = due."OrganizationId" AND work."Id" = due."Id"
            RETURNING work."OrganizationId", work."Id", work."Kind", work."ResourceKey", work."ReservationId",
                work."UploadOperationId", work."State", work."DueAtUtc", work."CreatedAtUtc", work."Attempts",
                work."LeaseId", work."LeaseUntilUtc", work."LastFailureCode"
            """, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = (object?)workId ?? DBNull.Value;
        command.Parameters.AddWithValue("lease", leaseId);
        QuotaMaintenanceWorkRecord? work = null;
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
                work = new()
                {
                    OrganizationId = reader.GetGuid(0), Id = reader.GetGuid(1), Kind = (QuotaMaintenanceKind)reader.GetInt32(2),
                    ResourceKey = reader.GetString(3), ReservationId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    UploadOperationId = reader.IsDBNull(5) ? null : reader.GetGuid(5), State = (QuotaMaintenanceState)reader.GetInt32(6),
                    DueAtUtc = reader.GetFieldValue<DateTimeOffset>(7), CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(8),
                    Attempts = reader.GetInt32(9), LeaseId = reader.GetGuid(10), LeaseUntilUtc = reader.GetFieldValue<DateTimeOffset>(11),
                    LastFailureCode = reader.IsDBNull(12) ? null : reader.GetString(12)
                };
        }
        await transaction.CommitAsync(token);
        return work is null ? null : new(work, leaseId);
    }

    internal async Task<bool> FinishAsync(QuotaMaintenanceClaim claim, QuotaMaintenanceResult result, CancellationToken cancellationToken)
    {
        int state = result.State switch
        {
            QuotaMaintenanceExecutionState.Completed => (int)QuotaMaintenanceState.Completed,
            QuotaMaintenanceExecutionState.Paused => (int)QuotaMaintenanceState.Paused,
            QuotaMaintenanceExecutionState.InProgress or QuotaMaintenanceExecutionState.Unavailable => (int)QuotaMaintenanceState.Queued,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        string? code = SafeCode(result.FailureCode);
        using CancellationTokenSource timeout = Deadline(cancellationToken);
        CancellationToken token = timeout.Token;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await BoundTransactionAsync(connection, transaction, token);
        await using NpgsqlCommand command = new("""
            UPDATE infrastructure.quota_maintenance_work
            SET "State" = @state, "LeaseId" = NULL, "LeaseUntilUtc" = NULL, "LastFailureCode" = @code,
                "DueAtUtc" = CASE WHEN @state = 3 THEN "DueAtUtc"
                    WHEN @state = 2 THEN statement_timestamp() + interval '60 seconds'
                    WHEN @progress THEN statement_timestamp() + interval '5 seconds'
                    ELSE statement_timestamp() + make_interval(secs => LEAST(300, 5 * power(2, LEAST("Attempts" - 1, 6)))) END
            WHERE "OrganizationId" = @organization AND "Id" = @id AND "State" = 1 AND "LeaseId" = @lease
                AND "LeaseUntilUtc" > statement_timestamp()
            """, connection, transaction) { CommandTimeout = 5 };
        BindClaim(command, claim);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("progress", result.State == QuotaMaintenanceExecutionState.InProgress);
        command.Parameters.Add("code", NpgsqlDbType.Varchar).Value = (object?)code ?? DBNull.Value;
        bool changed = await command.ExecuteNonQueryAsync(token) == 1;
        await transaction.CommitAsync(token);
        return changed;
    }

    internal async Task ReleaseAsync(QuotaMaintenanceClaim claim, CancellationToken cancellationToken) =>
        _ = await FinishAsync(claim, new(QuotaMaintenanceExecutionState.InProgress, "quota_maintenance_interrupted"), cancellationToken);

    internal async Task<bool> IsCompleteAsync(Guid workId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = Deadline(cancellationToken);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(timeout.Token);
        await using NpgsqlCommand command = new("""
            SELECT EXISTS (SELECT 1 FROM infrastructure.quota_maintenance_work WHERE "Id" = @id AND "State" = 3)
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", workId);
        return (bool)(await command.ExecuteScalarAsync(timeout.Token))!;
    }

    internal async Task<QuotaMaintenanceBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = Deadline(cancellationToken);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(timeout.Token);
        await using NpgsqlCommand command = new("""
            SELECT count(*), count(*) FILTER (WHERE "State" = 0 AND "Attempts" > 0),
                count(*) FILTER (WHERE "State" = 2), min("DueAtUtc")
            FROM infrastructure.quota_maintenance_work WHERE "State" <> 3
            """, connection) { CommandTimeout = 5 };
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token);
        await reader.ReadAsync(timeout.Token);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static void BindClaim(NpgsqlCommand command, QuotaMaintenanceClaim claim)
    {
        command.Parameters.AddWithValue("organization", claim.Work.OrganizationId);
        command.Parameters.AddWithValue("id", claim.Work.Id);
        command.Parameters.AddWithValue("lease", claim.LeaseId);
    }

    private static async Task BoundTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using NpgsqlCommand command = new("""
            SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '1s';
            SELECT set_config('app.organization_id', '', true), set_config('app.actor_id', '', true)
            """, connection, transaction) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync(token);
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return timeout;
    }

    private static string? SafeCode(string? code) => code is null ? null :
        code.Length is > 0 and <= 80 && code[0] is >= 'a' and <= 'z'
            && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                ? code : "quota_maintenance_unavailable";
}
