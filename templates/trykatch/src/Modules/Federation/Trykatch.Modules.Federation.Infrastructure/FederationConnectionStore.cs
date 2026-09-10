using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Trykatch.Modules.Federation.Application;
using Trykatch.Modules.Federation.Domain;

namespace Trykatch.Modules.Federation.Infrastructure;

internal sealed class FederationConnectionStore(
    FederationDatabaseOptions database,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : IFederationConnectionStore
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "Trykatch",
        "Federation",
        "ClientSecret",
        "v1");

    public async Task<IReadOnlyList<FederationConnection>> ListAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY name, id";
        List<FederationConnection> connections = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            connections.Add(Read(reader).Public);
        return connections;
    }

    public async Task<StoredFederationConnection?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " AND id = @id";
        command.Parameters.AddWithValue("id", id);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<FederationConnection?> CreateAsync(
        Guid id,
        string name,
        string issuer,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identity.federation_connections
                (id, name, issuer, client_id, protected_client_secret, enabled, created_at, updated_at)
            VALUES
                (@id, @name, @issuer, @client_id, @secret, false, @now, @now);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("client_id", clientId);
        command.Parameters.AddWithValue("secret", protector.Protect(clientSecret));
        command.Parameters.AddWithValue("now", now);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
        return (await GetAsync(id, cancellationToken))!.Public;
    }

    public async Task<FederationConnection?> UpdateAsync(
        StoredFederationConnection current,
        string name,
        string issuer,
        string clientId,
        string? clientSecret,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string? protectedSecret = string.IsNullOrWhiteSpace(clientSecret)
            ? current.ProtectedClientSecret
            : protector.Protect(clientSecret);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE identity.federation_connections
            SET name = @name,
                issuer = @issuer,
                client_id = @client_id,
                protected_client_secret = @secret,
                enabled = false,
                tested_configuration_hash = NULL,
                last_tested_at = NULL,
                last_test_result = NULL,
                updated_at = @now
            WHERE id = @id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("id", current.Public.Id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("client_id", clientId);
        command.Parameters.AddWithValue("secret", protectedSecret ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("now", now);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 0
                ? null
                : (await GetAsync(current.Public.Id, cancellationToken))?.Public;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    public async Task MarkTestedAsync(
        StoredFederationConnection current,
        bool successful,
        string message,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE identity.federation_connections
            SET tested_configuration_hash = @hash,
                last_tested_at = @now,
                last_test_result = @result
            WHERE id = @id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("id", current.Public.Id);
        command.Parameters.AddWithValue("hash", successful ? ConfigurationHash(current) : (object)DBNull.Value);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        command.Parameters.AddWithValue("result", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SetEnabledAsync(StoredFederationConnection current, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled && !string.Equals(current.TestedConfigurationHash, ConfigurationHash(current), StringComparison.Ordinal))
            return false;
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE identity.federation_connections
            SET enabled = @enabled, updated_at = @now
            WHERE id = @id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("id", current.Public.Id);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(StoredFederationConnection current, CancellationToken cancellationToken)
    {
        if (current.Public.Enabled)
            return false;
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE identity.federation_connections
            SET protected_client_secret = NULL, deleted_at = @now, updated_at = @now
            WHERE id = @id AND deleted_at IS NULL AND enabled = false;
            """;
        command.Parameters.AddWithValue("id", current.Public.Id);
        command.Parameters.AddWithValue("now", timeProvider.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public string UnprotectSecret(StoredFederationConnection connection) =>
        connection.ProtectedClientSecret is null ? string.Empty : protector.Unprotect(connection.ProtectedClientSecret);

    private static string ConfigurationHash(StoredFederationConnection connection)
    {
        string input = $"{connection.Public.Issuer}\n{connection.Public.ClientId}\n{connection.ProtectedClientSecret}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static StoredFederationConnection Read(NpgsqlDataReader reader)
    {
        string? protectedSecret = reader.IsDBNull(4) ? null : reader.GetString(4);
        FederationConnection value = new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            protectedSecret is not null,
            reader.GetBoolean(5),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9));
        return new(value, protectedSecret, reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private const string SelectSql = """
        SELECT id, name, issuer, client_id, protected_client_secret, enabled,
               tested_configuration_hash, last_tested_at, last_test_result, updated_at
        FROM identity.federation_connections
        WHERE deleted_at IS NULL
        """;
}
