using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
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
        string error)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform.outbox_messages
                ("Id", "Type", "Payload", "OccurredAt", "ProcessedAt", "Attempts", "LastError")
            VALUES
                (@id, @type, @payload::jsonb, @occurredAt, NULL, @attempts, @error);
            """;
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("type", "Trykatch.Security.ContractTest");
        command.Parameters.AddWithValue("payload", "{}");
        command.Parameters.AddWithValue("occurredAt", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("attempts", 3);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync();
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
