using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class QuotaUploadPersistenceIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string Resource = "documents.storage-bytes";
    private const string Queue = "infrastructure.quota_maintenance_work";
    private const string Upload = "app.module_upload_operations";
    private static readonly string[] Roles =
    [
        PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
        PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole,
        PostgresRuntimeRoleFixture.WorkCoordinatorRole
    ];
    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static PostgreSqlContainer _postgres = null!;
    private static string _ownerConnection = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _postgres = new PostgreSqlBuilder(Image).Build();
        await _postgres.StartAsync();
        _ownerConnection = _postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(_ownerConnection);
        (int Exit, string Output, string Error) migrated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(_ownerConnection);
        migrated.Exit.ShouldBe(0, migrated.Error + migrated.Output);
        (int Exit, string Output, string Error) repeated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(_ownerConnection);
        repeated.Exit.ShouldBe(0, repeated.Error + repeated.Output);
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    [TestMethod]
    public async Task ForwardMigratorCreatesForcedRlsQueueWithExactFiveProfilePrivileges()
    {
        await using NpgsqlConnection owner = new(_ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand rls = new("""
            SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = to_regclass(@relation)
            """, owner);
        rls.Parameters.AddWithValue("relation", Queue);
        await using NpgsqlDataReader reader = await rls.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("The forward migrator must create the queue relation.");
        reader.GetBoolean(0).ShouldBeTrue();
        reader.GetBoolean(1).ShouldBeTrue();
        await reader.DisposeAsync();

        foreach (string role in Roles)
        foreach (string privilege in Privileges)
        {
            await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, @relation, @privilege)", owner);
            grant.Parameters.AddWithValue("role", role);
            grant.Parameters.AddWithValue("relation", Queue);
            grant.Parameters.AddWithValue("privilege", privilege);
            bool expected = role == PostgresRuntimeRoleFixture.OrganizationRole && privilege == "INSERT"
                || role == PostgresRuntimeRoleFixture.WorkCoordinatorRole && privilege is "SELECT" or "UPDATE";
            (await grant.ExecuteScalarAsync()).ShouldBe(expected, $"Unexpected {privilege} privilege for {role} on {Queue}.");
        }
    }

    [TestMethod]
    [DataRow("body")]
    [DataRow("definer")]
    [DataRow("disabled-trigger")]
    [DataRow("runtime-execute")]
    public async Task InspectorRejectsTamperedRecoveryGuard(string mutation)
    {
        ServiceCollection services = new();
        ModuleCatalog catalog = services.AddModules(new ConfigurationBuilder().Build(), EnabledModules.All);
        PostgresIsolationInspection baseline = await PostgresIsolationInspector.InspectAsync(
            _ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole, catalog.Descriptors);
        baseline.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, baseline.Errors));

        await using NpgsqlConnection owner = new(_ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand read = new("""
            SELECT pg_get_functiondef('infrastructure.guard_quota_maintenance_transition()'::regprocedure)
            """, owner);
        string original = (string)(await read.ExecuteScalarAsync())!;
        string sql = mutation switch
        {
            "body" => """
                CREATE OR REPLACE FUNCTION infrastructure.guard_quota_maintenance_transition()
                RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';
                """,
            "definer" => "ALTER FUNCTION infrastructure.guard_quota_maintenance_transition() SECURITY DEFINER;",
            "disabled-trigger" => "ALTER TABLE infrastructure.quota_maintenance_work DISABLE TRIGGER guard_quota_maintenance_transition;",
            "runtime-execute" => $"GRANT EXECUTE ON FUNCTION infrastructure.guard_quota_maintenance_transition() TO {PostgresRuntimeRoleFixture.OrganizationRole};",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        try
        {
            await using NpgsqlCommand change = new(sql, owner);
            await change.ExecuteNonQueryAsync();
            PostgresIsolationInspection inspection = await PostgresIsolationInspector.InspectAsync(
                _ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole, catalog.Descriptors);
            inspection.IsValid.ShouldBeFalse();
            inspection.Errors.ShouldContain(error => error.Contains("guard_quota_maintenance_transition", StringComparison.Ordinal)
                || error.Contains("EXECUTE", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await using NpgsqlCommand restore = new(original + ";" + """

                ALTER TABLE infrastructure.quota_maintenance_work ENABLE TRIGGER guard_quota_maintenance_transition;
                REVOKE ALL ON FUNCTION infrastructure.guard_quota_maintenance_transition() FROM PUBLIC,
                """ + PostgresRuntimeRoleFixture.OrganizationRole + ";", owner);
            await restore.ExecuteNonQueryAsync();
        }

        PostgresIsolationInspection restored = await PostgresIsolationInspector.InspectAsync(
            _ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole, catalog.Descriptors);
        restored.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, restored.Errors));
    }

    [TestMethod]
    public async Task OrganizationCanAppendOnlyScopedQueuedWorkWithValidIntent()
    {
        Seed seed = await SeedAsync();
        Guid id = Guid.NewGuid();
        await using NpgsqlConnection organization = RuntimeConnection(
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await organization.OpenAsync();
        await using (NpgsqlTransaction transaction = await organization.BeginTransactionAsync())
        {
            await SetScopeAsync(organization, transaction, seed.Organization, seed.Actor);
            await InsertQueueAsync(organization, transaction, seed, id);
            await transaction.CommitAsync();
        }

        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            seed.Organization, seed.Actor, "SELECT count(*) FROM infrastructure.quota_maintenance_work");
        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            seed.Organization, seed.Actor, "UPDATE infrastructure.quota_maintenance_work SET \"Attempts\" = 1 WHERE \"Id\" = @id",
            ("id", id));
        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            seed.Organization, seed.Actor, "DELETE FROM infrastructure.quota_maintenance_work WHERE \"Id\" = @id", ("id", id));
    }

    [TestMethod]
    public async Task OrganizationQueueInsertRequiresMatchingScopeAndInitialQueuedShape()
    {
        Seed seed = await SeedAsync();
        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            seed.Organization, null, QueueInsertSql, ("organization", seed.Organization), ("id", Guid.NewGuid()),
            ("resource", Resource), ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)),
            ("created", DateTimeOffset.UtcNow), ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value));
        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            null, seed.Actor, QueueInsertSql, ("organization", seed.Organization), ("id", Guid.NewGuid()),
            ("resource", Resource), ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)),
            ("created", DateTimeOffset.UtcNow), ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value));
        await AssertRoleDeniedAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("@organization", "@foreignOrganization", StringComparison.Ordinal),
            ("foreignOrganization", Guid.NewGuid()), ("id", Guid.NewGuid()), ("resource", Resource),
            ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value));
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.InsufficientPrivilege,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("@initialState", "1", StringComparison.Ordinal),
            ("organization", seed.Organization), ("id", Guid.NewGuid()), ("resource", Resource),
            ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialAttempts", 0), ("initialFailure", DBNull.Value));
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.InsufficientPrivilege,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("@initialAttempts", "1", StringComparison.Ordinal),
            ("organization", seed.Organization), ("id", Guid.NewGuid()), ("resource", Resource),
            ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialFailure", DBNull.Value));
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.InsufficientPrivilege,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("@initialFailure", "'unexpected'", StringComparison.Ordinal),
            ("organization", seed.Organization), ("id", Guid.NewGuid()), ("resource", Resource),
            ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialAttempts", 0));
    }

    [TestMethod]
    public async Task CoordinatorCanLeaseQueueButCannotInsertDeleteOrReadBusinessQuotaData()
    {
        Seed seed = await SeedAsync();
        Guid id = Guid.NewGuid();
        await InsertQueueAsOrganizationAsync(seed, id);
        await using NpgsqlConnection coordinator = RuntimeConnection(
            PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
        await coordinator.OpenAsync();
        await using (NpgsqlTransaction transaction = await coordinator.BeginTransactionAsync())
        {
            await using NpgsqlCommand read = new("SELECT \"OrganizationId\" FROM infrastructure.quota_maintenance_work WHERE \"Id\" = @id", coordinator, transaction);
            read.Parameters.AddWithValue("id", id);
            (await read.ExecuteScalarAsync()).ShouldBe(seed.Organization);
            await using NpgsqlCommand update = new("""
                UPDATE infrastructure.quota_maintenance_work
                SET "State" = 1, "Attempts" = 1, "LeaseId" = @lease, "LeaseUntilUtc" = @until
                WHERE "Id" = @id
                """, coordinator, transaction);
            update.Parameters.AddWithValue("lease", Guid.NewGuid());
            update.Parameters.AddWithValue("until", DateTimeOffset.UtcNow.AddMinutes(1));
            update.Parameters.AddWithValue("id", id);
            (await update.ExecuteNonQueryAsync()).ShouldBe(1);
            await transaction.CommitAsync();
        }

        await AssertCoordinatorDeniedAsync("INSERT INTO infrastructure.quota_maintenance_work DEFAULT VALUES");
        await AssertCoordinatorDeniedAsync("DELETE FROM infrastructure.quota_maintenance_work WHERE false");
        await AssertCoordinatorDeniedAsync("SELECT count(*) FROM app.organization_quota_reservations");
    }

    [TestMethod]
    public async Task QueueConstraintsProtectIntentReferencesAndCompletedState()
    {
        Seed seed = await SeedAsync();
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.CheckViolation,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("1, @resource", "99, @resource", StringComparison.Ordinal),
            ("organization", seed.Organization), ("id", Guid.NewGuid()), ("resource", Resource),
            ("reservation", seed.Reservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value));
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.ForeignKeyViolation,
            seed.Organization, seed.Actor, QueueInsertSql.Replace("@reservation", "@missingReservation", StringComparison.Ordinal),
            ("organization", seed.Organization), ("id", Guid.NewGuid()), ("resource", Resource),
            ("missingReservation", seed.ForeignReservation), ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value));

        Guid id = Guid.NewGuid();
        await InsertQueueAsOrganizationAsync(seed, id);
        await AssertCoordinatorFailureAsync(PostgresErrorCodes.CheckViolation,
            $"UPDATE {Queue} SET \"ResourceKey\" = 'projects.documents' WHERE \"Id\" = '{id}'");
        await using NpgsqlConnection coordinator = RuntimeConnection(
            PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
        await coordinator.OpenAsync();
        await using (NpgsqlTransaction transaction = await coordinator.BeginTransactionAsync())
        await using (NpgsqlCommand complete = new("""
            UPDATE infrastructure.quota_maintenance_work SET "State" = 3 WHERE "Id" = @id
            """, coordinator, transaction))
        {
            complete.Parameters.AddWithValue("id", id);
            (await complete.ExecuteNonQueryAsync()).ShouldBe(1);
            await transaction.CommitAsync();
        }
        await AssertCoordinatorFailureAsync(PostgresErrorCodes.CheckViolation,
            $"UPDATE {Queue} SET \"State\" = 0 WHERE \"Id\" = '{id}'");
    }

    [TestMethod]
    public async Task UploadPolicyIsRequiredForNewRowsAndImmutableAfterAdmission()
    {
        Seed seed = await SeedAsync(effectKind: 2);
        Guid operation = Guid.NewGuid();
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.CheckViolation,
            seed.Organization, seed.Actor, UploadInsertSql.Replace("@policyKey, @policyVersion", "NULL, NULL", StringComparison.Ordinal),
            UploadParameters(seed, operation).Where(parameter => parameter.Name is not ("policyKey" or "policyVersion")).ToArray());

        await using NpgsqlConnection organization = RuntimeConnection(
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await organization.OpenAsync();
        await using (NpgsqlTransaction transaction = await organization.BeginTransactionAsync())
        {
            await SetScopeAsync(organization, transaction, seed.Organization, seed.Actor);
            await using NpgsqlCommand insert = new(UploadInsertSql, organization, transaction);
            AddParameters(insert, UploadParameters(seed, operation));
            (await insert.ExecuteNonQueryAsync()).ShouldBe(1);
            await transaction.CommitAsync();
        }

        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.CheckViolation,
            seed.Organization, seed.Actor, "UPDATE app.module_upload_operations SET \"UploadPolicyKey\" = 'changed.policy' WHERE \"OperationId\" = @operation",
            ("operation", operation));
        await AssertRoleFailureAsync(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword,
            PostgresErrorCodes.CheckViolation,
            seed.Organization, seed.Actor, "UPDATE app.module_upload_operations SET \"UploadPolicyVersion\" = 2 WHERE \"OperationId\" = @operation",
            ("operation", operation));
    }

    private static async Task<Seed> SeedAsync(int effectKind = 1)
    {
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        Guid entitlementVersion = Guid.NewGuid();
        Guid otherEntitlementVersion = Guid.NewGuid();
        Guid reservation = Guid.NewGuid();
        Guid foreignReservation = Guid.NewGuid();
        await using NpgsqlConnection owner = new(_ownerConnection);
        await owner.OpenAsync();
        foreach ((Guid id, string slug) in new[] { (organization, $"quota-{organization:N}"), (otherOrganization, $"quota-{otherOrganization:N}") })
        {
            await using NpgsqlCommand createOrganization = new("""
                INSERT INTO platform.organizations ("Id", "Name", "Slug", "IsActive", "CreatedAt")
                VALUES (@id, @slug, @slug, true, CURRENT_TIMESTAMP)
                """, owner);
            createOrganization.Parameters.AddWithValue("id", id);
            createOrganization.Parameters.AddWithValue("slug", slug);
            await createOrganization.ExecuteNonQueryAsync();
        }
        foreach ((Guid currentOrganization, Guid version) in new[]
        {
            (organization, entitlementVersion), (otherOrganization, otherEntitlementVersion)
        })
        {
            await using NpgsqlCommand entitlement = new("""
                INSERT INTO app.organization_entitlements
                    ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version", "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
                VALUES (@organization, @resource, 'documents', 1, 1, 100000, @version, 'integration-test', 1, @hash, CURRENT_TIMESTAMP)
                """, owner);
            entitlement.Parameters.AddWithValue("organization", currentOrganization);
            entitlement.Parameters.AddWithValue("resource", Resource);
            entitlement.Parameters.AddWithValue("version", version);
            entitlement.Parameters.AddWithValue("hash", new string('a', 64));
            await entitlement.ExecuteNonQueryAsync();
        }
        foreach ((Guid currentOrganization, Guid currentReservation, Guid version) in new[]
        {
            (organization, reservation, entitlementVersion), (otherOrganization, foreignReservation, otherEntitlementVersion)
        })
        {
            await using NpgsqlCommand reservationInsert = new("""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion", "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                VALUES (@organization, @id, @resource, @actor, 3, @operation, @version, @effectKind, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + interval '1 hour', NULL)
                """, owner);
            reservationInsert.Parameters.AddWithValue("organization", currentOrganization);
            reservationInsert.Parameters.AddWithValue("id", currentReservation);
            reservationInsert.Parameters.AddWithValue("resource", Resource);
            reservationInsert.Parameters.AddWithValue("actor", actor);
            reservationInsert.Parameters.AddWithValue("operation", Guid.NewGuid());
            reservationInsert.Parameters.AddWithValue("version", version);
            reservationInsert.Parameters.AddWithValue("effectKind", effectKind);
            await reservationInsert.ExecuteNonQueryAsync();
        }
        return new Seed(organization, otherOrganization, actor, reservation, foreignReservation);
    }

    private static async Task InsertQueueAsOrganizationAsync(Seed seed, Guid id)
    {
        await using NpgsqlConnection organization = RuntimeConnection(
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await organization.OpenAsync();
        await using NpgsqlTransaction transaction = await organization.BeginTransactionAsync();
        await SetScopeAsync(organization, transaction, seed.Organization, seed.Actor);
        await InsertQueueAsync(organization, transaction, seed, id);
        await transaction.CommitAsync();
    }

    private static async Task InsertQueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Seed seed, Guid id)
    {
        await using NpgsqlCommand insert = new(QueueInsertSql, connection, transaction);
        AddParameters(insert,
        [
            ("organization", seed.Organization), ("id", id), ("resource", Resource), ("reservation", seed.Reservation),
            ("due", DateTimeOffset.UtcNow.AddMinutes(5)), ("created", DateTimeOffset.UtcNow),
            ("initialState", 0), ("initialAttempts", 0), ("initialFailure", DBNull.Value)
        ]);
        (await insert.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private static async Task AssertRoleDeniedAsync(string role, string password, Guid? organization, Guid? actor,
        string sql, params (string Name, object Value)[] parameters)
    {
        PostgresException denied = await RunAndCaptureRoleFailureAsync(role, password, organization, actor, sql, parameters);
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task AssertRoleFailureAsync(string role, string password, string expectedSqlState,
        Guid? organization, Guid? actor,
        string sql, params (string Name, object Value)[] parameters)
    {
        PostgresException failure = await RunAndCaptureRoleFailureAsync(role, password, organization, actor, sql, parameters);
        failure.SqlState.ShouldBe(expectedSqlState);
    }

    private static async Task<PostgresException> RunAndCaptureRoleFailureAsync(string role, string password, Guid? organization, Guid? actor,
        string sql, (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = RuntimeConnection(role, password);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, organization, actor);
        await using NpgsqlCommand command = new(sql, connection, transaction);
        AddParameters(command, parameters);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        await transaction.RollbackAsync();
        return denied;
    }

    private static async Task AssertCoordinatorDeniedAsync(string sql)
    {
        PostgresException denied = await RunAndCaptureCoordinatorFailureAsync(sql);
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task AssertCoordinatorFailureAsync(string expectedSqlState, string sql)
    {
        PostgresException failure = await RunAndCaptureCoordinatorFailureAsync(sql);
        failure.SqlState.ShouldBe(expectedSqlState);
    }

    private static async Task<PostgresException> RunAndCaptureCoordinatorFailureAsync(string sql)
    {
        await using NpgsqlConnection coordinator = RuntimeConnection(
            PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
        await coordinator.OpenAsync();
        await using NpgsqlTransaction transaction = await coordinator.BeginTransactionAsync();
        await using NpgsqlCommand command = new(sql, coordinator, transaction);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        await transaction.RollbackAsync();
        return denied;
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? organization, Guid? actor)
    {
        await using NpgsqlCommand scope = new("""
            SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)
            """, connection, transaction);
        scope.Parameters.AddWithValue("organization", organization?.ToString() ?? string.Empty);
        scope.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await scope.ExecuteNonQueryAsync();
    }

    private static NpgsqlConnection RuntimeConnection(string role, string password) =>
        new(new NpgsqlConnectionStringBuilder(_ownerConnection) { Username = role, Password = password }.ConnectionString);

    private static void AddParameters(NpgsqlCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
    }

    private static (string Name, object Value)[] UploadParameters(Seed seed, Guid operation) =>
    [
        ("organization", seed.Organization), ("resource", Resource), ("operation", operation), ("actor", seed.Actor),
        ("membership", Guid.NewGuid()), ("reservation", seed.Reservation), ("subject", Guid.NewGuid()),
        ("objectKey", $"_module-uploads/v1/orgs/{seed.Organization:N}/objects/{operation:N}"),
        ("storageNamespace", new string('b', 64)), ("length", 3L), ("hash", new string('c', 64)),
        ("metadata", new string('d', 64)), ("policyKey", "documents.upload"), ("policyVersion", 1)
    ];

    private const string QueueInsertSql = """
        INSERT INTO infrastructure.quota_maintenance_work
            ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State", "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
        VALUES (@organization, @id, 1, @resource, @reservation, NULL, @initialState, @due, @created, @initialAttempts, NULL, NULL, @initialFailure)
        """;

    private const string UploadInsertSql = """
        INSERT INTO app.module_upload_operations
            ("OrganizationId", "ResourceKey", "OperationId", "ActorId", "MembershipId", "ReservationId", "SubjectId", "ObjectKey", "StorageNamespaceFingerprint", "Length", "Sha256", "MetadataFingerprint", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc", "UploadPolicyKey", "UploadPolicyVersion")
        VALUES (@organization, @resource, @operation, @actor, @membership, @reservation, @subject, @objectKey, @storageNamespace, @length, @hash, @metadata, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + interval '1 hour', NULL, @policyKey, @policyVersion)
        """;

    private sealed record Seed(Guid Organization, Guid OtherOrganization, Guid Actor, Guid Reservation, Guid ForeignReservation);
}
