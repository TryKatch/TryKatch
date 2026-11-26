using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Infrastructure.Organizations;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class PostgresRuntimeGrantAtomicityTests
{
    [TestMethod]
    public async Task FailedGrantRebuildPreservesThePreviousPrivileges()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:19.0-alpine3.23@sha256:40ad7e457013448b5522c6c5d30dccb8248959c41adc0e56c1785be4293bbd67").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        (int Exit, string Output, string Error) migration =
            await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connectionString);
        migration.Exit.ShouldBe(0, $"Initial migration failed.{Environment.NewLine}{migration.Error}{Environment.NewLine}{migration.Output}");

        RuntimeDatabaseRoles runtimeRoles = new(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole);
        await using NpgsqlConnection owner = new(connectionString);
        await owner.OpenAsync();
        await using (NpgsqlCommand seedPrivilege = new(
            $"GRANT DELETE ON app.projects TO {PostgresRuntimeRoleFixture.OrganizationRole}", owner))
            await seedPrivilege.ExecuteNonQueryAsync();
        await using (NpgsqlCommand corruptCatalog = new(
            "INSERT INTO platform.module_data_resources (schema_name, table_name, module_id, declaration) VALUES ('app', 'invalid_fixture', 'test-fixture', '{}'::jsonb)", owner))
            await corruptCatalog.ExecuteNonQueryAsync();

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            RuntimeRoleProvisioner.ProvisionAsync(connectionString, runtimeRoles));
        error.Message.ShouldContain("approved schema");

        await using NpgsqlCommand stillGranted = new(
            "SELECT has_table_privilege(@role, 'app.projects', 'DELETE')", owner);
        stillGranted.Parameters.AddWithValue("role", PostgresRuntimeRoleFixture.OrganizationRole);
        (await stillGranted.ExecuteScalarAsync()).ShouldBe(true);
    }
}
