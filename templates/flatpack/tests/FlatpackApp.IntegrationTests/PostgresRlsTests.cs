using Npgsql;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace FlatpackApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresRlsTests
{
    [TestMethod]
    public async Task RuntimeRoleCannotCrossOrganizationAccessControlBoundary()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.0-alpine3.22").Build();
        await postgres.StartAsync();
        await using (PlatformDbContext platform = new(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(postgres.GetConnectionString()).Options))
        {
            await platform.Database.MigrateAsync();
        }

        Guid organizationA = Guid.CreateVersion7();
        Guid organizationB = Guid.CreateVersion7();
        Guid actorA = Guid.CreateVersion7();
        Guid actorB = Guid.CreateVersion7();
        Guid membershipA = Guid.CreateVersion7();
        Guid membershipB = Guid.CreateVersion7();
        Guid roleA = Guid.CreateVersion7();
        Guid roleB = Guid.CreateVersion7();

        await using (NpgsqlConnection owner = new(postgres.GetConnectionString()))
        {
            await owner.OpenAsync();
            await using NpgsqlCommand setup = owner.CreateCommand();
            setup.CommandText = """
                INSERT INTO platform.organizations ("Id", "Name", "Slug", "IsActive", "CreatedAt") VALUES
                  (@organization_a, 'Organization A', 'organization-a', true, now()),
                  (@organization_b, 'Organization B', 'organization-b', true, now());
                INSERT INTO platform.memberships ("Id", "OrganizationId", "UserId", "Status", "JoinedAt") VALUES
                  (@membership_a, @organization_a, @actor_a, 1, now()),
                  (@membership_b, @organization_b, @actor_b, 1, now());
                INSERT INTO platform.roles ("Id", "OrganizationId", "Name", "IsSystem") VALUES
                  (@role_a, @organization_a, 'Reader A', false),
                  (@role_b, @organization_b, 'Reader B', false);
                INSERT INTO platform.membership_roles ("MembershipId", "RoleId") VALUES
                  (@membership_a, @role_a), (@membership_b, @role_b);
                INSERT INTO platform.role_permissions ("RoleId", "Permission") VALUES
                  (@role_a, 'projects.read'), (@role_b, 'projects.read');
                CREATE ROLE flatpack_access_runtime LOGIN PASSWORD 'runtime-access-test' NOBYPASSRLS;
                GRANT USAGE ON SCHEMA platform TO flatpack_access_runtime;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform TO flatpack_access_runtime;
                """;
            setup.Parameters.AddWithValue("organization_a", organizationA);
            setup.Parameters.AddWithValue("organization_b", organizationB);
            setup.Parameters.AddWithValue("actor_a", actorA);
            setup.Parameters.AddWithValue("actor_b", actorB);
            setup.Parameters.AddWithValue("membership_a", membershipA);
            setup.Parameters.AddWithValue("membership_b", membershipB);
            setup.Parameters.AddWithValue("role_a", roleA);
            setup.Parameters.AddWithValue("role_b", roleB);
            await setup.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder connectionBuilder = new(postgres.GetConnectionString())
        {
            Username = "flatpack_access_runtime",
            Password = "runtime-access-test"
        };
        await using NpgsqlConnection runtime = new(connectionBuilder.ConnectionString);
        await runtime.OpenAsync();

        await using (NpgsqlTransaction actorTransaction = await runtime.BeginTransactionAsync())
        {
            await SetContextAsync(runtime, actorTransaction, actorA, null, false);
            (await ScalarCountAsync(runtime, actorTransaction, "SELECT count(*) FROM platform.memberships")).ShouldBe(1);
            (await ScalarCountAsync(runtime, actorTransaction, "SELECT count(*) FROM platform.roles")).ShouldBe(1);
            (await ScalarCountAsync(runtime, actorTransaction, "SELECT count(*) FROM platform.role_permissions")).ShouldBe(1);
        }

        await using (NpgsqlTransaction organizationTransaction = await runtime.BeginTransactionAsync())
        {
            await SetContextAsync(runtime, organizationTransaction, actorA, organizationA, false);
            (await ScalarCountAsync(runtime, organizationTransaction, "SELECT count(*) FROM platform.roles")).ShouldBe(1);
            await using NpgsqlCommand crossOrganizationInsert = runtime.CreateCommand();
            crossOrganizationInsert.Transaction = organizationTransaction;
            crossOrganizationInsert.CommandText = "INSERT INTO platform.roles (\"Id\", \"OrganizationId\", \"Name\", \"IsSystem\") VALUES (@id, @organization, 'Blocked', false)";
            crossOrganizationInsert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            crossOrganizationInsert.Parameters.AddWithValue("organization", organizationB);
            NpgsqlException exception = await Should.ThrowAsync<NpgsqlException>(crossOrganizationInsert.ExecuteNonQueryAsync());
            exception.Message.ShouldContain("row-level security");
        }

        await using (NpgsqlTransaction platformTransaction = await runtime.BeginTransactionAsync())
        {
            await SetContextAsync(runtime, platformTransaction, actorA, null, true);
            (await ScalarCountAsync(runtime, platformTransaction, "SELECT count(*) FROM platform.roles")).ShouldBe(2);
        }
    }

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

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actorId, Guid? organizationId, bool platformAdministrator)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.actor_id', @actor, true), set_config('app.organization_id', @organization, true), set_config('app.platform_admin', @platform_admin, true)";
        command.Parameters.AddWithValue("actor", actorId.ToString());
        command.Parameters.AddWithValue("organization", organizationId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("platform_admin", platformAdministrator ? "true" : "false");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
