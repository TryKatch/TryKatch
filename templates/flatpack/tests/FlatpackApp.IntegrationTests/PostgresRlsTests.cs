using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace FlatpackApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresRlsTests
{
    [TestMethod]
    public async Task RuntimeRoleCannotCrossOrganizationBoundary()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22").Build();
        await postgres.StartAsync();

        Guid organizationA = Guid.CreateVersion7();
        Guid organizationB = Guid.CreateVersion7();
        await using (NpgsqlConnection owner = new(postgres.GetConnectionString()))
        {
            await owner.OpenAsync();
            await using NpgsqlCommand setup = owner.CreateCommand();
            setup.CommandText = """
                CREATE SCHEMA app;
                CREATE TABLE app.projects ("Id" uuid PRIMARY KEY, "OrganizationId" uuid NOT NULL, "Name" text NOT NULL);
                ALTER TABLE app.projects ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.projects FORCE ROW LEVEL SECURITY;
                CREATE POLICY organization_isolation ON app.projects
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE ROLE flatpack_test_runtime LOGIN PASSWORD 'runtime-test' NOBYPASSRLS;
                GRANT USAGE ON SCHEMA app TO flatpack_test_runtime;
                GRANT SELECT, INSERT, UPDATE, DELETE ON app.projects TO flatpack_test_runtime;
                """;
            await setup.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder connectionBuilder = new(postgres.GetConnectionString())
        {
            Username = "flatpack_test_runtime",
            Password = "runtime-test"
        };
        await using NpgsqlConnection runtime = new(connectionBuilder.ConnectionString);
        await runtime.OpenAsync();
        await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
        await using NpgsqlCommand command = runtime.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.organization_id', @organization, true)";
        command.Parameters.AddWithValue("organization", organizationA.ToString());
        await command.ExecuteNonQueryAsync();

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO app.projects VALUES (@id, @organization, 'Allowed')";
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization", organizationA);
        await command.ExecuteNonQueryAsync();

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO app.projects VALUES (@id, @organization, 'Blocked')";
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization", organizationB);
        NpgsqlException exception = await Should.ThrowAsync<NpgsqlException>(command.ExecuteNonQueryAsync());
        exception.Message.ShouldContain("row-level security");
    }
}
