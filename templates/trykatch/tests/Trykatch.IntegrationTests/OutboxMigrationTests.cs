using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Identity;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OutboxMigrationTests
{
    private const string PreviousMigration = "20260907201519_AddRecoverableLifecycle";

    [TestMethod]
    public void RecoveryMigrationSnapshotsMatchProductionModelsWithoutOpeningADatabase()
    {
        const string unavailable = "Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none";
        using ApplicationDbContext application = CreateContext(unavailable);
        using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(unavailable).Options);

        application.Database.HasPendingModelChanges().ShouldBeFalse();
        platform.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [TestMethod]
    public async Task ClassificationMigrationPurgesLegacyExceptionMessagesWithoutLosingDeliveryState()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();

        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);

        await using (IdentityDbContext identity = new(
                         new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options))
            await identity.Database.MigrateAsync();

        await using (PlatformDbContext platform = new(
                         new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options))
            await platform.Database.MigrateAsync();

        await using (ApplicationDbContext context = CreateContext(connectionString))
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        Guid messageId = Guid.CreateVersion7();
        const string secret = "password=legacy-secret-must-not-survive";
        await InsertLegacyFailureAsync(connectionString, messageId, secret);
        Guid exhaustedId = Guid.CreateVersion7();
        Guid processedId = Guid.CreateVersion7();
        await InsertLegacyFailureAsync(connectionString, exhaustedId, "password=terminal-secret", attempts: 10);
        await InsertLegacyFailureAsync(connectionString, processedId, "password=completed-secret", attempts: 10,
            processedAt: DateTimeOffset.UtcNow);

        await using (ApplicationDbContext context = CreateContext(connectionString))
            await context.GetService<IMigrator>().MigrateAsync("20260911133245_ClassifyOutboxFailures");
        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("UPDATE platform.outbox_messages SET \"LastErrorType\" = @type WHERE \"Id\" = @id", connection);
            command.Parameters.AddWithValue("type", new string('T', 500));
            command.Parameters.AddWithValue("id", exhaustedId);
            await command.ExecuteNonQueryAsync();
        }

        await using (ApplicationDbContext context = CreateContext(connectionString))
            await context.Database.MigrateAsync();

        (int attempts, string? errorCode, string? errorType, string? legacyError) = await ReadMigratedFailureAsync(
            connectionString,
            messageId);
        attempts.ShouldBe(3);
        errorCode.ShouldBe("legacy_unclassified");
        errorType.ShouldBe("legacy_exception");
        legacyError.ShouldBeNull();

        string storedRow = $"{errorCode} {errorType} {legacyError}";
        storedRow.ShouldNotContain(secret);
        (bool exhausted, long recoveryEvents) = await ReadRecoveryStateAsync(connectionString, exhaustedId);
        exhausted.ShouldBeTrue();
        recoveryEvents.ShouldBe(1);
        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"FailureType\" FROM platform.outbox_recovery_events WHERE \"MessageId\" = @id", connection);
            command.Parameters.AddWithValue("id", exhaustedId);
            ((string)(await command.ExecuteScalarAsync())!).ShouldBe(new string('T', 240));
        }
        (bool processedExhausted, long processedRecoveryEvents) = await ReadRecoveryStateAsync(connectionString, processedId);
        processedExhausted.ShouldBeFalse();
        processedRecoveryEvents.ShouldBe(0);

        Guid rollingDeploymentMessageId = Guid.CreateVersion7();
        const string rollingDeploymentSecret = "token=old-worker-secret-must-not-survive";
        await InsertLegacyFailureAsync(connectionString, rollingDeploymentMessageId, rollingDeploymentSecret);
        (_, string? rollingCode, string? rollingType, string? rollingLegacyError) =
            await ReadMigratedFailureAsync(connectionString, rollingDeploymentMessageId);
        rollingCode.ShouldBe("legacy_unclassified");
        rollingType.ShouldBe("legacy_exception");
        rollingLegacyError.ShouldBeNull();
    }

    private static ApplicationDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
        [new ProjectsModelContributor(), new DocumentsModelContributor()],
        moduleCatalog: new ModuleCatalog([new ProjectsModule(), new DocumentsModule()]));

    private static async Task InsertLegacyFailureAsync(
        string connectionString,
        Guid messageId,
        string error,
        int attempts = 3,
        DateTimeOffset? processedAt = null)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform.outbox_messages
                ("Id", "Type", "Payload", "OccurredAt", "ProcessedAt", "Attempts", "LastError")
            VALUES
                (@id, @type, @payload::jsonb, @occurredAt, @processedAt, @attempts, @error);
            """;
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("type", "Trykatch.Security.ContractTest");
        command.Parameters.AddWithValue("payload", "{}");
        command.Parameters.AddWithValue("occurredAt", DateTimeOffset.UtcNow);
        command.Parameters.Add("processedAt", NpgsqlDbType.TimestampTz).Value = (object?)processedAt ?? DBNull.Value;
        command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(bool Exhausted, long RecoveryEvents)> ReadRecoveryStateAsync(string connectionString, Guid messageId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT message."ExhaustedAt" IS NOT NULL,
              (SELECT count(*) FROM platform.outbox_recovery_events event WHERE event."MessageId" = message."Id")
            FROM platform.outbox_messages message WHERE message."Id" = @id
            """;
        command.Parameters.AddWithValue("id", messageId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.GetBoolean(0), reader.GetInt64(1));
    }

    private static async Task<(int Attempts, string? ErrorCode, string? ErrorType, string? LegacyError)> ReadMigratedFailureAsync(
        string connectionString,
        Guid messageId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Attempts", "LastErrorCode", "LastErrorType", "LastError"
            FROM platform.outbox_messages
            WHERE "Id" = @id;
            """;
        command.Parameters.AddWithValue("id", messageId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
