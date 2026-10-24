using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ModuleIdempotencyMigrationIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task ReceiptMigrationIsRepeatableWithForcedRlsAndExactFiveRuntimeProfiles()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await MigrateAsync(ownerConnection);
        await MigrateAsync(ownerConnection);
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand relation = new("""
            SELECT relrowsecurity, relforcerowsecurity FROM pg_class
            WHERE oid = to_regclass('app.mutation_receipts')
            """, owner);
        await using (NpgsqlDataReader reader = await relation.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue("The forward receipt migration must create the reserved relation.");
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
                await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, 'app.mutation_receipts', @privilege)", owner);
                grant.Parameters.AddWithValue("role", role);
                grant.Parameters.AddWithValue("privilege", privilege);
                bool expected = role == PostgresRuntimeRoleFixture.OrganizationRole && privilege is "SELECT" or "INSERT" or "UPDATE";
                (await grant.ExecuteScalarAsync()).ShouldBe(expected, $"Unexpected {privilege} for {role}.");
            }
        }
    }

    [TestMethod]
    public async Task ReceiptRlsRequiresBothOrganizationAndActorForEveryReadAndWrite()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await MigrateAsync(ownerConnection);
        Guid organization = Guid.NewGuid(), actor = Guid.NewGuid();
        await using NpgsqlConnection runtime = new(new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.OrganizationRole,
            Password = PostgresRuntimeRoleFixture.OrganizationPassword
        }.ConnectionString);
        await runtime.OpenAsync();
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, organization, actor);
            (await InsertAsync(runtime, transaction, organization, actor)).ShouldBe(1);
            await transaction.CommitAsync();
        }
        foreach ((Guid? scopeOrganization, Guid? scopeActor) in new (Guid?, Guid?)[]
                 {
                     (Guid.NewGuid(), actor), (organization, Guid.NewGuid()),
                     (organization, null), (null, actor), (null, null)
                 })
        {
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetScopeAsync(runtime, transaction, scopeOrganization, scopeActor);
            await using NpgsqlCommand read = new("SELECT count(*) FROM app.mutation_receipts", runtime, transaction);
            (await read.ExecuteScalarAsync()).ShouldBe(0L);
            await using NpgsqlCommand update = new("UPDATE app.mutation_receipts SET \"StatusCode\" = 202", runtime, transaction);
            (await update.ExecuteNonQueryAsync()).ShouldBe(0);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertAsync(runtime, transaction, organization, actor));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, organization, actor);
            await using NpgsqlCommand read = new("SELECT count(*) FROM app.mutation_receipts", runtime, transaction);
            (await read.ExecuteScalarAsync()).ShouldBe(1L);
            await using NpgsqlCommand moveActor = new("UPDATE app.mutation_receipts SET \"ActorId\" = @actor", runtime, transaction);
            moveActor.Parameters.AddWithValue("actor", Guid.NewGuid());
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => moveActor.ExecuteNonQueryAsync());
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [TestMethod]
    public async Task ReceiptTableRejectsMalformedOrUnboundedRawSqlWrites()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await MigrateAsync(ownerConnection);
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using (NpgsqlTransaction transaction = await owner.BeginTransactionAsync())
        {
            (await InsertAsync(owner, transaction, Guid.NewGuid(), Guid.NewGuid())).ShouldBe(1);
            await transaction.CommitAsync();
        }
        foreach (string invalidUpdate in new[]
                 {
                     "UPDATE app.mutation_receipts SET \"StatusCode\" = 400",
                     "UPDATE app.mutation_receipts SET \"Body\" = decode(repeat('00', 1048577), 'hex')",
                     "UPDATE app.mutation_receipts SET \"KeyHash\" = repeat('G', 64)",
                     "UPDATE app.mutation_receipts SET \"ActorId\" = '00000000-0000-0000-0000-000000000000'::uuid",
                     "UPDATE app.mutation_receipts SET \"ExpiresAtUtc\" = \"CompletedAtUtc\" + interval '59 seconds'",
                     "UPDATE app.mutation_receipts SET \"ExpiresAtUtc\" = \"CompletedAtUtc\" + interval '25 hours'",
                     "UPDATE app.mutation_receipts SET \"ResourceKey\" = 'projects'",
                     "UPDATE app.mutation_receipts SET \"Location\" = '//outside.test/private'"
                 })
        {
            await using NpgsqlTransaction transaction = await owner.BeginTransactionAsync();
            await using NpgsqlCommand command = new(invalidUpdate, owner, transaction);
            PostgresException rejected = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            rejected.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
    }

    private static async Task MigrateAsync(string ownerConnection)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
        var result = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        result.Exit.ShouldBe(0, result.Error + result.Output);
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? organization, Guid? actor)
    {
        await using NpgsqlCommand command = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
        command.Parameters.AddWithValue("organization", organization?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, Guid actor)
    {
        await using NpgsqlCommand command = new("""
            INSERT INTO app.mutation_receipts
              ("OrganizationId", "ActorId", "ModuleId", "OperationId", "KeyHash", "RequestHash",
               "StatusCode", "Body", "CompletedAtUtc", "ExpiresAtUtc")
            VALUES (@organization, @actor, 'projects', 'Projects_Create', @hash, @hash,
                    201, ''::bytea, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + interval '1 hour')
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("hash", Hash);
        return await command.ExecuteNonQueryAsync();
    }
}
