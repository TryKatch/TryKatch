using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class RealtimeHistoryMigrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private static readonly Guid?[] MissingActors = [null, Guid.Empty];
    private static readonly string[] HistoryMutations = ["UPDATE app.realtime_history SET \"PolicyVersion\" = 2", "DELETE FROM app.realtime_history"];
    private static readonly string[] Tables = ["realtime_streams", "realtime_history"];
    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static readonly string[] Roles = [PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
        PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.WorkCoordinatorRole];

    [TestMethod]
    public async Task MigratorReservesHistoryAndCounterWithExactForcedRlsAndAppendOnlyGrants()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        await MigrateAsync(postgres.GetConnectionString());
        await MigrateAsync(postgres.GetConnectionString());
        await using NpgsqlConnection owner = new(postgres.GetConnectionString());
        await owner.OpenAsync();
        foreach (string table in Tables)
        {
            await using NpgsqlCommand relation = new("SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = to_regclass(@relation)", owner);
            relation.Parameters.AddWithValue("relation", "app." + table);
            await using (NpgsqlDataReader reader = await relation.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).ShouldBeTrue("The forward migration must create the reserved relation.");
                reader.GetBoolean(0).ShouldBeTrue();
                reader.GetBoolean(1).ShouldBeTrue();
            }
            foreach (string role in Roles)
            foreach (string privilege in Privileges)
            {
                await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, @relation, @privilege)", owner);
                grant.Parameters.AddWithValue("role", role);
                grant.Parameters.AddWithValue("relation", "app." + table);
                grant.Parameters.AddWithValue("privilege", privilege);
                bool expected = role == PostgresRuntimeRoleFixture.OrganizationRole
                    && (privilege is "SELECT" or "INSERT" || table == "realtime_streams" && privilege == "UPDATE");
                (await grant.ExecuteScalarAsync()).ShouldBe(expected, $"Unexpected {privilege} for {role} on {table}.");
            }
        }
    }

    [TestMethod]
    public async Task RawOrganizationRoleCannotDiscloseForeignHistoryOrRewriteCommittedReferences()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        await MigrateAsync(postgres.GetConnectionString());
        Guid organization = Guid.NewGuid(), other = Guid.NewGuid(), actor = Guid.NewGuid();
        await using NpgsqlConnection runtime = new(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = PostgresRuntimeRoleFixture.OrganizationRole, Password = PostgresRuntimeRoleFixture.OrganizationPassword }.ConnectionString);
        await runtime.OpenAsync();
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, organization, actor);
            (await InsertHistoryAsync(runtime, transaction, organization)).ShouldBe(1);
            await transaction.CommitAsync();
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, other, actor);
            foreach (string table in Tables)
            {
                await using NpgsqlCommand read = new($"SELECT count(*) FROM app.{table}", runtime, transaction);
                (await read.ExecuteScalarAsync()).ShouldBe(0L);
            }
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertHistoryAsync(runtime, transaction, organization));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, other, actor);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertHistoryAsync(runtime, transaction, organization, createStream: false));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        foreach (Guid? missingActor in MissingActors)
        {
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetScopeAsync(runtime, transaction, other, missingActor);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => InsertHistoryAsync(runtime, transaction, other, createStream: false));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        foreach (string statement in HistoryMutations)
        {
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetScopeAsync(runtime, transaction, organization, actor);
            await using NpgsqlCommand command = new(statement, runtime, transaction);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static async Task MigrateAsync(string connection)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connection);
        var migrated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connection);
        migrated.Exit.ShouldBe(0, migrated.Error + migrated.Output);
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, Guid? actor)
    {
        await using NpgsqlCommand command = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
        command.Parameters.AddWithValue("organization", organization.ToString());
        command.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, bool createStream = true)
    {
        string prefix = createStream ? "INSERT INTO app.realtime_streams (\"OrganizationId\", \"Sequence\") VALUES (@organization, 1);" : string.Empty;
        await using NpgsqlCommand command = new(prefix + """
            INSERT INTO app.realtime_history ("OrganizationId", "Sequence", "ModuleId", "EventKey", "SchemaVersion", "EventId",
                "PolicyOwnerModuleId", "PolicyKey", "PolicyVersion", "AudienceKind", "SourceMessageId", "CreatedAtUtc")
            VALUES (@organization, 1, 'projects', 'projects.created', 1, @event,
                'projects', 'projects.realtime-read', 1, 0, @source, CURRENT_TIMESTAMP)
            RETURNING 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("event", Guid.NewGuid());
        command.Parameters.AddWithValue("source", Guid.NewGuid());
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
