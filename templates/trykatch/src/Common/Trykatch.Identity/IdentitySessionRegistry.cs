using Npgsql;
using NpgsqlTypes;
using Trykatch.Application.Identity;

namespace Trykatch.Identity;

/// <summary>Narrow Identity operations deliberately bypass tracked user state and unrelated SaveChanges.</summary>
internal sealed class IdentitySessionRegistry(string connectionString, TimeProvider clock) : IAccountSessions
{
    private const string ValidSession = """
        s."SessionHash" = @hash AND s."UserId" = @actor AND s."SecurityStamp" = @stamp
        AND u."Id" = s."UserId" AND u."SecurityStamp" = @stamp
        AND s."RevokedAt" IS NULL AND s."ExpiresAt" > @now
        """;
    private const string StateColumns = "s.\"OrganizationId\", s.\"WorkspaceGeneration\", s.\"ExpiresAt\"";

    public async Task<AccountSessionProof> IssueAsync(Guid actorId, string sessionHash, string securityStamp,
        DateTimeOffset expiresAt, AccountSessionProof? replacing = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        AccountSessionProof proof = new(actorId, sessionHash, securityStamp, expiresAt.ToUniversalTime());
        if (!IsValid(proof, now)) throw new ArgumentException("Invalid session issuance.");
        if (replacing?.SessionHash == sessionHash) throw new InvalidOperationException("A fresh session identity is required.");
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (NpgsqlCommand issue = new("""
            INSERT INTO identity.account_sessions ("SessionHash", "UserId", "SecurityStamp", "CreatedAt", "LastSeenAt", "ExpiresAt", "WorkspaceGeneration")
            SELECT @hash, u."Id", @stamp, @now, @now, @expiry, @generation FROM identity."AspNetUsers" u
            WHERE u."Id" = @actor AND u."SecurityStamp" = @stamp
            """, connection, transaction))
        {
            AddProof(issue, proof, now);
            issue.Parameters.AddWithValue("expiry", proof.TicketExpiresAt);
            issue.Parameters.AddWithValue("generation", Guid.NewGuid());
            if (await issue.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Session issuance requires a current account.");
        }
        if (replacing is not null)
            await RevokeAsync(connection, transaction, replacing, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return proof;
    }

    public async Task<AccountSessionState?> ValidateAsync(AccountSessionProof proof, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!IsValid(proof, now)) return null;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        AccountSessionState state;
        DateTimeOffset lastSeen;
        await using (NpgsqlCommand read = new($"SELECT {StateColumns}, s.\"LastSeenAt\" FROM identity.account_sessions s, identity.\"AspNetUsers\" u WHERE {ValidSession}", connection))
        {
            AddProof(read, proof, now);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            state = ReadState(reader);
            lastSeen = reader.GetFieldValue<DateTimeOffset>(3);
        }
        if (lastSeen > now.AddMinutes(-1)) return state;
        await using NpgsqlCommand touch = new($"""
            UPDATE identity.account_sessions s SET "LastSeenAt" = GREATEST(s."LastSeenAt", @now)
            FROM identity."AspNetUsers" u WHERE {ValidSession} RETURNING {StateColumns}
            """, connection);
        AddProof(touch, proof, now);
        return await ReadStateAsync(touch, cancellationToken);
    }

    public async Task<AccountSessionState?> RenewAsync(AccountSessionProof proof, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!IsValid(proof, now) || expiresAt <= now) return null;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand renew = new($"""
            UPDATE identity.account_sessions s SET "ExpiresAt" = GREATEST(s."ExpiresAt", @expiry),
                "LastSeenAt" = CASE WHEN s."LastSeenAt" <= @now - INTERVAL '1 minute' THEN @now ELSE s."LastSeenAt" END
            FROM identity."AspNetUsers" u WHERE {ValidSession} RETURNING {StateColumns}
            """, connection);
        AddProof(renew, proof, now);
        renew.Parameters.AddWithValue("expiry", expiresAt.ToUniversalTime());
        return await ReadStateAsync(renew, cancellationToken);
    }

    public async Task<AccountSessionState?> BindWorkspaceAsync(AccountSessionProof proof, Guid? organizationId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!IsValid(proof, now) || organizationId == Guid.Empty) return null;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand bind = new($"""
            UPDATE identity.account_sessions s SET "OrganizationId" = @organization, "WorkspaceGeneration" = @generation
            FROM identity."AspNetUsers" u WHERE {ValidSession} RETURNING {StateColumns}
            """, connection);
        AddProof(bind, proof, now);
        bind.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)organizationId ?? DBNull.Value);
        bind.Parameters.AddWithValue("generation", Guid.NewGuid());
        return await ReadStateAsync(bind, cancellationToken);
    }

    public async Task RevokeAsync(AccountSessionProof proof, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await RevokeAsync(connection, null, proof, clock.GetUtcNow(), cancellationToken);
    }

    private static async Task RevokeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        AccountSessionProof proof, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (proof.ActorId == Guid.Empty || !IsHash(proof.SessionHash)) return;
        await using NpgsqlCommand revoke = new("""
            UPDATE identity.account_sessions SET "RevokedAt" = @now
            WHERE "SessionHash" = @hash AND "UserId" = @actor AND "RevokedAt" IS NULL
            """, connection, transaction);
        revoke.Parameters.AddWithValue("hash", proof.SessionHash);
        revoke.Parameters.AddWithValue("actor", proof.ActorId);
        revoke.Parameters.AddWithValue("now", now);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsValid(AccountSessionProof proof, DateTimeOffset now) =>
        proof.ActorId != Guid.Empty && IsHash(proof.SessionHash)
        && proof.SecurityStamp is { Length: > 0 and <= 256 } && proof.TicketExpiresAt > now;

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AddProof(NpgsqlCommand command, AccountSessionProof proof, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("hash", proof.SessionHash);
        command.Parameters.AddWithValue("actor", proof.ActorId);
        command.Parameters.AddWithValue("stamp", proof.SecurityStamp);
        command.Parameters.AddWithValue("now", now);
    }

    private static AccountSessionState ReadState(NpgsqlDataReader reader) =>
        new(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2));

    private static async Task<AccountSessionState?> ReadStateAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }
}
