using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OrganizationCacheMigrationIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task MigratorReservesCacheWatermarkAndProvisionsOnlyExactOrganizationPrivileges()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
        var first = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        first.Exit.ShouldBe(0, first.Error + first.Output);
        var repeated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        repeated.Exit.ShouldBe(0, repeated.Error + repeated.Output);

        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand relation = new("""
            SELECT relrowsecurity, relforcerowsecurity FROM pg_class
            WHERE oid = to_regclass('app.cache_invalidations')
            """, owner);
        await using (NpgsqlDataReader reader = await relation.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue("The forward cache migration must create the reserved relation.");
            reader.GetBoolean(0).ShouldBeTrue();
            reader.GetBoolean(1).ShouldBeTrue();
        }
        foreach (string role in new[]
                 {
                     PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
                     PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole,
                     PostgresRuntimeRoleFixture.WorkCoordinatorRole
                 })
        {
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
            {
                await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, 'app.cache_invalidations', @privilege)", owner);
                grant.Parameters.AddWithValue("role", role);
                grant.Parameters.AddWithValue("privilege", privilege);
                bool expected = role == PostgresRuntimeRoleFixture.OrganizationRole && privilege is "SELECT" or "INSERT" or "UPDATE";
                (await grant.ExecuteScalarAsync()).ShouldBe(expected, $"Unexpected {privilege} for {role}.");
            }
        }
    }

    [TestMethod]
    public async Task CacheWatermarkRlsRejectsAnotherOrganizationAndMissingActor()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
        var migrated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        migrated.Exit.ShouldBe(0, migrated.Error + migrated.Output);
        Guid firstOrganization = Guid.NewGuid(), otherOrganization = Guid.NewGuid(), actor = Guid.NewGuid();
        await using NpgsqlConnection runtime = new(new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.OrganizationRole,
            Password = PostgresRuntimeRoleFixture.OrganizationPassword
        }.ConnectionString);
        await runtime.OpenAsync();
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, firstOrganization, actor);
            (await InsertAsync(runtime, transaction, firstOrganization)).ShouldBe(1);
            await transaction.CommitAsync();
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, otherOrganization, actor);
            await using NpgsqlCommand read = new("SELECT count(*) FROM app.cache_invalidations", runtime, transaction);
            (await read.ExecuteScalarAsync()).ShouldBe(0L);
            await using NpgsqlCommand update = new("UPDATE app.cache_invalidations SET \"Generation\" = @generation", runtime, transaction);
            update.Parameters.AddWithValue("generation", Guid.NewGuid());
            (await update.ExecuteNonQueryAsync()).ShouldBe(0);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertAsync(runtime, transaction, firstOrganization));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, otherOrganization, null);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertAsync(runtime, transaction, otherOrganization));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, Guid? actor)
    {
        await using NpgsqlCommand command = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
        command.Parameters.AddWithValue("organization", organization.ToString());
        command.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization)
    {
        await using NpgsqlCommand command = new("""
            INSERT INTO app.cache_invalidations ("OrganizationId", "KeyHash", "Generation", "UpdatedAtUtc")
            VALUES (@organization, @hash, @generation, CURRENT_TIMESTAMP)
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("hash", Hash);
        command.Parameters.AddWithValue("generation", Guid.NewGuid());
        return await command.ExecuteNonQueryAsync();
    }
}
