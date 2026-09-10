using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace TrykatchApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresRuntimeRoleFixtureTests
{
    [TestMethod]
    public async Task ConcurrentSetupFromDifferentDatabaseConnectionStringsUsesOneClusterLockAndConvergesRoles()
    {
        string? configuredPostgres = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
        await using PostgreSqlContainer? postgres = string.IsNullOrWhiteSpace(configuredPostgres)
            ? new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build()
            : null;
        if (postgres is not null) await postgres.StartAsync();
        string administratorConnection = postgres?.GetConnectionString() ?? configuredPostgres!;

        string firstDatabaseConnection = new NpgsqlConnectionStringBuilder(administratorConnection)
        {
            Database = $"fixture_a_{Guid.NewGuid():N}"
        }.ConnectionString;
        string secondDatabaseConnection = new NpgsqlConnectionStringBuilder(administratorConnection)
        {
            Database = $"fixture_b_{Guid.NewGuid():N}"
        }.ConnectionString;

        await Task.WhenAll(
            PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(firstDatabaseConnection),
            PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(secondDatabaseConnection));

        NpgsqlConnectionStringBuilder maintenanceConnection = new(administratorConnection) { Database = "postgres" };
        await using NpgsqlConnection connection = new(maintenanceConnection.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT rolname, rolcanlogin, rolinherit, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
            FROM pg_roles
            WHERE rolname IN ('trykatch_org_runtime', 'trykatch_platform_runtime', 'trykatch_identity_runtime', 'trykatch_outbox_worker')
            ORDER BY rolname;
            """;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> roles = [];
        while (await reader.ReadAsync())
        {
            roles.Add(reader.GetString(0));
            reader.GetBoolean(1).ShouldBeTrue($"{reader.GetString(0)} must be usable by the runtime");
            for (int ordinal = 2; ordinal < reader.FieldCount; ordinal++)
                reader.GetBoolean(ordinal).ShouldBeFalse($"{reader.GetString(0)} must retain restrictive role attributes");
        }

        roles.ShouldBe([
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OutboxRole,
            PostgresRuntimeRoleFixture.PlatformRole
        ]);
    }
}
