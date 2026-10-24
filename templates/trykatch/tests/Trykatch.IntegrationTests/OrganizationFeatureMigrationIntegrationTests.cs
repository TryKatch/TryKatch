using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OrganizationFeatureMigrationIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string EnvironmentName = "production";
    private const string FeatureKey = "projects.reads";

    [TestMethod]
    public async Task MigratorCreatesForcedRlsFeatureRelationsAndOnlyExactPrivilegesIdempotently()
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
        foreach (string table in Tables)
        {
            await using NpgsqlCommand rls = new("""
                SELECT relrowsecurity, relforcerowsecurity
                FROM pg_class WHERE oid = to_regclass(@relation)
                """, owner);
            rls.Parameters.AddWithValue("relation", $"platform.{table}");
            await using NpgsqlDataReader reader = await rls.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue($"Expected platform.{table} after migration.");
            reader.GetBoolean(0).ShouldBeTrue();
            reader.GetBoolean(1).ShouldBeTrue();
            await reader.DisposeAsync();

            foreach ((string role, string[] expected) in ExpectedPrivileges(table))
            {
                foreach (string privilege in Privileges)
                {
                    await using NpgsqlCommand grant = new(
                        "SELECT has_table_privilege(@role, @relation, @privilege)", owner);
                    grant.Parameters.AddWithValue("role", role);
                    grant.Parameters.AddWithValue("relation", $"platform.{table}");
                    grant.Parameters.AddWithValue("privilege", privilege);
                    (await grant.ExecuteScalarAsync()).ShouldBe(expected.Contains(privilege, StringComparer.Ordinal),
                        $"Unexpected {privilege} for {role} on platform.{table}.");
                }
            }
        }
    }

    [TestMethod]
    public async Task OrganizationRoleCanSelectEnvironmentAndOnlyItsOwnOverrideWithActorScope()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedOrganizationAsync(ownerConnection, organization, "feature-org-one");
        await SeedOrganizationAsync(ownerConnection, otherOrganization, "feature-org-two");
        await SeedControlAsync(ownerConnection);
        await SeedOverrideAsync(ownerConnection, organization, enabled: true);
        await SeedOverrideAsync(ownerConnection, otherOrganization, enabled: false);

        await using NpgsqlConnection runtime = RuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await runtime.OpenAsync();
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, organization, actor);
            await using NpgsqlCommand environment = new("""
                SELECT "DefaultEnabled", "ForceDisabled" FROM platform.feature_environment_controls
                WHERE "Environment" = @environment AND "FeatureKey" = @feature
                """, runtime, transaction);
            environment.Parameters.AddWithValue("environment", EnvironmentName);
            environment.Parameters.AddWithValue("feature", FeatureKey);
            await using NpgsqlDataReader environmentReader = await environment.ExecuteReaderAsync();
            (await environmentReader.ReadAsync()).ShouldBeTrue();
            environmentReader.IsDBNull(0).ShouldBeTrue();
            environmentReader.GetBoolean(1).ShouldBeFalse();
            await environmentReader.DisposeAsync();

            await using (NpgsqlCommand overrides = new("""
                SELECT "OrganizationId", "Enabled" FROM platform.organization_feature_overrides
                WHERE "Environment" = @environment AND "FeatureKey" = @feature
                ORDER BY "OrganizationId"
                """, runtime, transaction))
            {
                overrides.Parameters.AddWithValue("environment", EnvironmentName);
                overrides.Parameters.AddWithValue("feature", FeatureKey);
                await using NpgsqlDataReader overrideReader = await overrides.ExecuteReaderAsync();
                (await overrideReader.ReadAsync()).ShouldBeTrue();
                overrideReader.GetGuid(0).ShouldBe(organization);
                overrideReader.GetBoolean(1).ShouldBeTrue();
                (await overrideReader.ReadAsync()).ShouldBeFalse("A scoped organization must not see another organization's override.");
            }
            await transaction.CommitAsync();
        }

        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetScopeAsync(runtime, transaction, organization, null);
            foreach (string table in new[] { "feature_environment_controls", "organization_feature_overrides" })
            {
                await using NpgsqlCommand read = new($"SELECT count(*) FROM platform.{table}", runtime, transaction);
                (await read.ExecuteScalarAsync()).ShouldBe(0L, $"Actorless safe SELECT must not expose {table}.");
            }
            await transaction.CommitAsync();
        }
    }

    [TestMethod]
    public async Task PlatformActorCanUpdateControlAfterAnotherEditorAndCannotForgeHistoryActor()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        await SeedControlAsync(ownerConnection);
        Guid currentActor = Guid.NewGuid();
        Guid previousEditor = Guid.NewGuid();
        await using NpgsqlConnection runtime = RuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.PlatformRole, PostgresRuntimeRoleFixture.PlatformPassword);
        await runtime.OpenAsync();
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetActorAsync(runtime, transaction, previousEditor);
            await InsertEnvironmentHistoryAsync(runtime, transaction, previousEditor);
            await transaction.CommitAsync();
        }
        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetActorAsync(runtime, transaction, currentActor);
            await using NpgsqlCommand update = new("""
                UPDATE platform.feature_environment_controls
                SET "DefaultEnabled" = true, "Version" = @version, "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "Environment" = @environment AND "FeatureKey" = @feature
                """, runtime, transaction);
            update.Parameters.AddWithValue("version", Guid.NewGuid());
            update.Parameters.AddWithValue("environment", EnvironmentName);
            update.Parameters.AddWithValue("feature", FeatureKey);
            (await update.ExecuteNonQueryAsync()).ShouldBe(1,
                "A new editor must update a control without matching a prior editor identity.");
            await InsertEnvironmentHistoryAsync(runtime, transaction, currentActor);
            await transaction.CommitAsync();
        }

        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetActorAsync(runtime, transaction, currentActor);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(
                () => InsertEnvironmentHistoryAsync(runtime, transaction, previousEditor));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync();
        }
    }

    [TestMethod]
    public async Task RawMalformedFeatureRowsAreRejectedByShapeVersionAndOrganizationConstraints()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();

        await AssertRejectedAsync(owner, """
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES ('Prod/Bad', 'projects.reads', NULL, false, @version, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.CheckViolation, ("version", Guid.NewGuid()));
        await AssertRejectedAsync(owner, """
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES ('production', 'bad key', NULL, false, @version, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.CheckViolation, ("version", Guid.NewGuid()));
        await AssertRejectedAsync(owner, """
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES ('production', 'projects.reads', NULL, false, @version, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.CheckViolation, ("version", Guid.Empty));
        await AssertRejectedAsync(owner, """
            INSERT INTO platform.organization_feature_overrides
                ("OrganizationId", "Environment", "FeatureKey", "Enabled", "Version", "UpdatedAtUtc")
            VALUES (@organization, 'production', 'projects.reads', true, @version, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.ForeignKeyViolation,
            ("organization", Guid.NewGuid()), ("version", Guid.NewGuid()));
        await AssertRejectedAsync(owner, """
            INSERT INTO platform.feature_control_history
                ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                 "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES (@id, 'production', 'projects.reads', NULL, @actor, @version, @version,
                    NULL, true, NULL, false, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.CheckViolation,
            ("id", Guid.NewGuid()), ("actor", Guid.NewGuid()), ("version", Guid.NewGuid()));
        await AssertRejectedAsync(owner, """
            INSERT INTO platform.feature_control_history
                ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                 "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES (@id, 'production', 'projects.reads', @organization, @actor, NULL, @version,
                    NULL, true, NULL, NULL, CURRENT_TIMESTAMP)
            """, PostgresErrorCodes.ForeignKeyViolation, ("id", Guid.NewGuid()), ("organization", Guid.NewGuid()),
            ("actor", Guid.NewGuid()), ("version", Guid.NewGuid()));

        Guid retainedOrganization = Guid.NewGuid();
        await SeedOrganizationAsync(ownerConnection, retainedOrganization, "feature-retention");
        await SeedOverrideAsync(ownerConnection, retainedOrganization, enabled: true);
        await AssertRejectedAsync(owner, "DELETE FROM platform.organizations WHERE \"Id\" = @id",
            PostgresErrorCodes.RestrictViolation, ("id", retainedOrganization));
        await using (NpgsqlCommand retainHistory = new("""
            DELETE FROM platform.organization_feature_overrides WHERE "OrganizationId" = @organization;
            INSERT INTO platform.feature_control_history
                ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                 "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES (@id, 'production', 'projects.reads', @organization, @actor, NULL, @version,
                    NULL, true, NULL, NULL, CURRENT_TIMESTAMP)
            """, owner))
        {
            retainHistory.Parameters.AddWithValue("organization", retainedOrganization);
            retainHistory.Parameters.AddWithValue("id", Guid.NewGuid());
            retainHistory.Parameters.AddWithValue("actor", Guid.NewGuid());
            retainHistory.Parameters.AddWithValue("version", Guid.NewGuid());
            await retainHistory.ExecuteNonQueryAsync();
        }
        await AssertRejectedAsync(owner, "DELETE FROM platform.organizations WHERE \"Id\" = @id",
            PostgresErrorCodes.RestrictViolation, ("id", retainedOrganization));
    }

    [TestMethod]
    public async Task RuntimeRolesCannotExceedFeatureControlGrantsOrActorPolicies()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedOrganizationAsync(ownerConnection, organization, "feature-role-denials");
        await SeedControlAsync(ownerConnection);
        await SeedHistoryAsync(ownerConnection);

        await AssertRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.PlatformPassword, """
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES ('production', 'projects.writes', true, false, @version, CURRENT_TIMESTAMP)
            """, actorId: string.Empty, parameters: [("version", Guid.NewGuid())]);

        await AssertRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, """
            INSERT INTO platform.organization_feature_overrides
                ("OrganizationId", "Environment", "FeatureKey", "Enabled", "Version", "UpdatedAtUtc")
            VALUES (@organization, 'production', 'projects.writes', true, @version, CURRENT_TIMESTAMP)
            """, organizationId: organization, actor: actor,
            parameters: [("organization", organization), ("version", Guid.NewGuid())]);

        await AssertRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.PlatformPassword,
            "DELETE FROM platform.feature_environment_controls WHERE \"Environment\" = 'production' AND \"FeatureKey\" = 'projects.reads'",
            actorId: actor.ToString());
        await AssertRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.PlatformPassword,
            "UPDATE platform.feature_control_history SET \"Enabled\" = false WHERE \"Environment\" = 'production'",
            actorId: actor.ToString());

        foreach ((string role, string password) in new[]
                 {
                     (PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.IdentityPassword),
                     (PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.OutboxPassword),
                     (PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword)
                 })
        {
            await AssertRoleDeniedAsync(ownerConnection, role, password,
                "SELECT \"Environment\" FROM platform.feature_environment_controls", actorId: actor.ToString());
        }
    }

    private static readonly string[] Tables =
        ["feature_environment_controls", "organization_feature_overrides", "feature_control_history"];

    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

    private static IEnumerable<(string Role, string[] Expected)> ExpectedPrivileges(string table)
    {
        yield return (PostgresRuntimeRoleFixture.OrganizationRole,
            table is "feature_environment_controls" or "organization_feature_overrides" ? ["SELECT"] : []);
        yield return (PostgresRuntimeRoleFixture.PlatformRole, table switch
        {
            "feature_environment_controls" => ["SELECT", "INSERT", "UPDATE"],
            "organization_feature_overrides" => ["SELECT", "INSERT", "UPDATE"],
            "feature_control_history" => ["SELECT", "INSERT"],
            _ => []
        });
        yield return (PostgresRuntimeRoleFixture.IdentityRole, []);
        yield return (PostgresRuntimeRoleFixture.OutboxRole, []);
        yield return (PostgresRuntimeRoleFixture.WorkCoordinatorRole, []);
    }

    private static async Task<PostgreSqlContainer> MigratedDatabaseAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
        var migration = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        migration.Exit.ShouldBe(0, migration.Error + migration.Output);
        return postgres;
    }

    private static NpgsqlConnection RuntimeConnection(string ownerConnection, string role, string password) =>
        new(new NpgsqlConnectionStringBuilder(ownerConnection) { Username = role, Password = password }.ConnectionString);

    private static async Task SeedOrganizationAsync(string connectionString, Guid id, string slug)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.organizations ("Id", "Name", "Slug", "IsActive", "CreatedAt")
            VALUES (@id, @name, @slug, true, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", slug);
        command.Parameters.AddWithValue("slug", slug);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedControlAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES (@environment, @feature, NULL, false, @version, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("environment", EnvironmentName);
        command.Parameters.AddWithValue("feature", FeatureKey);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedHistoryAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.feature_control_history
                ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                 "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES (@id, @environment, @feature, NULL, @actor, NULL, @version,
                    NULL, true, NULL, false, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("environment", EnvironmentName);
        command.Parameters.AddWithValue("feature", FeatureKey);
        command.Parameters.AddWithValue("actor", Guid.NewGuid());
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedOverrideAsync(string connectionString, Guid organization, bool enabled)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.organization_feature_overrides
                ("OrganizationId", "Environment", "FeatureKey", "Enabled", "Version", "UpdatedAtUtc")
            VALUES (@organization, @environment, @feature, @enabled, @version, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("environment", EnvironmentName);
        command.Parameters.AddWithValue("feature", FeatureKey);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, Guid? actor)
    {
        await using NpgsqlCommand command = new("""
            SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organization.ToString());
        command.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetActorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor)
    {
        await using NpgsqlCommand command = new("SELECT set_config('app.actor_id', @actor, true)", connection, transaction);
        command.Parameters.AddWithValue("actor", actor.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertEnvironmentHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid actor)
    {
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.feature_control_history
                ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                 "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES (@id, @environment, @feature, NULL, @actor, NULL, @version,
                    NULL, true, NULL, false, CURRENT_TIMESTAMP)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("environment", EnvironmentName);
        command.Parameters.AddWithValue("feature", FeatureKey);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRejectedAsync(
        NpgsqlConnection connection,
        string sql,
        string expectedSqlState,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        PostgresException exception = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        exception.SqlState.ShouldBe(expectedSqlState);
        await transaction.RollbackAsync();
    }

    private static async Task AssertRoleDeniedAsync(
        string ownerConnection,
        string role,
        string password,
        string sql,
        Guid? organizationId = null,
        Guid? actor = null,
        string? actorId = null,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = RuntimeConnection(ownerConnection, role, password);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        if (organizationId is Guid organization)
            await SetScopeAsync(connection, transaction, organization, actor);
        else if (actorId is not null)
            await using (NpgsqlCommand setActor = new("SELECT set_config('app.actor_id', @actor, true)", connection, transaction))
            {
                setActor.Parameters.AddWithValue("actor", actorId);
                await setActor.ExecuteNonQueryAsync();
            }

        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        await transaction.RollbackAsync();
    }
}
