using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

public sealed partial class OrganizationQuotaMigrationIntegrationTests
{
    private const string QuotaResourceKey = "documents.storage-bytes";

    [TestMethod]
    public async Task OrganizationRoleSeesOnlyScopedQuotaRowsAndActorlessOrUnscopedReadsSeeNone()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await using NpgsqlConnection runtime = QuotaRuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await runtime.OpenAsync();

        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetQuotaScopeAsync(runtime, transaction, organization, actor);
            foreach (string table in Tables)
            {
                await using NpgsqlCommand read = new($"SELECT DISTINCT \"OrganizationId\" FROM app.{table}", runtime, transaction);
                await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();
                (await reader.ReadAsync()).ShouldBeTrue($"Expected a scoped row in {table}.");
                reader.GetGuid(0).ShouldBe(organization);
                (await reader.ReadAsync()).ShouldBeFalse($"The organization role must not see the other organization's {table} row.");
            }

            await transaction.CommitAsync();
        }

        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetQuotaScopeAsync(runtime, transaction, organization, actor: null);
            foreach (string table in Tables)
            {
                await using NpgsqlCommand read = new($"SELECT count(*) FROM app.{table}", runtime, transaction);
                (await read.ExecuteScalarAsync()).ShouldBe(0L, $"Actorless reads must not expose {table}.");
            }

            await transaction.CommitAsync();
        }

        await using (NpgsqlTransaction transaction = await runtime.BeginTransactionAsync())
        {
            await SetQuotaScopeAsync(runtime, transaction, organization: null, actor: actor);
            foreach (string table in Tables)
            {
                await using NpgsqlCommand read = new($"SELECT count(*) FROM app.{table}", runtime, transaction);
                (await read.ExecuteScalarAsync()).ShouldBe(0L, $"Reads without an organization must not expose {table}.");
            }

            await transaction.CommitAsync();
        }
    }

    [TestMethod]
    public async Task PlatformEntitlementProjectionIsScopedAndOrganizationRoleCannotUpdateIt()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await using (NpgsqlConnection platform = QuotaRuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.PlatformRole, PostgresRuntimeRoleFixture.PlatformPassword))
        {
            await platform.OpenAsync();
            await using NpgsqlTransaction transaction = await platform.BeginTransactionAsync();
            await SetQuotaScopeAsync(platform, transaction, organization, actor);
            await using (NpgsqlCommand select = new("SELECT \"OrganizationId\" FROM app.organization_entitlements", platform, transaction))
            await using (NpgsqlDataReader reader = await select.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).ShouldBeTrue();
                reader.GetGuid(0).ShouldBe(organization);
                (await reader.ReadAsync()).ShouldBeFalse("Platform reads must remain scoped to the requested organization.");
            }

            await using NpgsqlCommand updateForeign = new("""
                UPDATE app.organization_entitlements SET "Unit" = 2
                WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource
                """, platform, transaction);
            updateForeign.Parameters.AddWithValue("organization", otherOrganization);
            updateForeign.Parameters.AddWithValue("resource", QuotaResourceKey);
            (await updateForeign.ExecuteNonQueryAsync()).ShouldBe(0,
                "A platform update scoped to another organization must not affect its entitlement.");
            await transaction.CommitAsync();
        }

        await AssertQuotaRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.organization_entitlements SET "Unit" = 2
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource
            """, ("organization", organization), ("resource", QuotaResourceKey));
    }

    [TestMethod]
    public async Task OrganizationRoleCannotUpdateOrDeleteAppendOnlyUsage()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.organization_usage SET "Units" = 2
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "EffectId" = @effect
            """, ("organization", organization), ("resource", QuotaResourceKey), ("effect", seed.UsageIds[organization]));
        await AssertQuotaRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            DELETE FROM app.organization_usage
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "EffectId" = @effect
            """, ("organization", organization), ("resource", QuotaResourceKey), ("effect", seed.UsageIds[organization]));
    }

    [TestMethod]
    public async Task OrganizationUsageRejectsCrossOrganizationAndActorlessInserts()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            VALUES (@organization, @resource, @effect, @source, NULL, @reservation, @actor, 1, CURRENT_TIMESTAMP)
            """, ("organization", otherOrganization), ("resource", QuotaResourceKey), ("effect", Guid.NewGuid()),
            ("source", Guid.NewGuid()), ("reservation", seed.ReservationIds[otherOrganization]), ("actor", actor));
        await AssertQuotaRoleDeniedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor: null, sql: """
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            VALUES (@organization, @resource, @effect, @source, NULL, @reservation, @actor, 1, CURRENT_TIMESTAMP)
            """, ("organization", organization), ("resource", QuotaResourceKey), ("effect", Guid.NewGuid()),
            ("source", Guid.NewGuid()), ("reservation", seed.ReservationIds[organization]), ("actor", actor));
    }

    [TestMethod]
    public async Task IdentityOutboxAndCoordinatorRolesCannotReadOrganizationUsage()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        foreach ((string role, string password) in new[]
        {
            (PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.IdentityPassword),
            (PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.OutboxPassword),
            (PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword)
        })
        {
            await using NpgsqlConnection runtime = QuotaRuntimeConnection(ownerConnection, role, password);
            await runtime.OpenAsync();
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await using NpgsqlCommand read = new("SELECT count(*) FROM app.organization_usage", runtime, transaction);
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() => read.ExecuteScalarAsync());
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} must not query organization usage.");
            await transaction.RollbackAsync();
        }
    }

    [TestMethod]
    public async Task EntitlementUnitIsImmutableAfterProjectionCreation()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.PlatformPassword, organization, actor, """
            UPDATE app.organization_entitlements SET "Unit" = 2
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource
            """, ("organization", organization), ("resource", QuotaResourceKey));
    }

    [TestMethod]
    public async Task ReservationEffectKindIsImmutableAfterCreation()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.organization_quota_reservations SET "EffectKind" = 1
            WHERE "OrganizationId" = @organization AND "Id" = @reservation
            """, ("organization", organization), ("reservation", seed.ReservationIds[organization]));
    }

    [TestMethod]
    public async Task ClosedReservationCannotBeReopened()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.organization_quota_reservations SET "State" = 0, "ClosedAtUtc" = NULL
            WHERE "OrganizationId" = @organization AND "Id" = @reservation
            """, ("organization", organization), ("reservation", seed.ClosedReservationIds[organization]));
    }

    [TestMethod]
    public async Task FencingUploadOperationCannotReturnToAdmitted()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.module_upload_operations SET "State" = 0
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "OperationId" = @operation
            """, ("organization", organization), ("resource", QuotaResourceKey), ("operation", seed.UploadOperationIds[organization]));
    }

    [TestMethod]
    public async Task AdmittedUploadPayloadFingerprintsCannotChange()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.module_upload_operations SET "MetadataFingerprint" = @fingerprint
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "OperationId" = @operation
            """, ("organization", organization), ("resource", QuotaResourceKey), ("operation", seed.AdmittedUploadOperationIds[organization]),
            ("fingerprint", new string('b', 64)));
        await AssertQuotaTransitionRejectedAsync(ownerConnection, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword, organization, actor, """
            UPDATE app.module_upload_operations SET "StorageNamespaceFingerprint" = @fingerprint
            WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "OperationId" = @operation
            """, ("organization", organization), ("resource", QuotaResourceKey), ("operation", seed.AdmittedUploadOperationIds[organization]),
            ("fingerprint", new string('c', 64)));
    }

    [TestMethod]
    public async Task QuotaGuardsAllowForwardTransitionsAndIdempotentClosedUpdates()
    {
        await using PostgreSqlContainer postgres = await MigratedQuotaDatabaseAsync();
        string ownerConnection = postgres.GetConnectionString();
        Guid organization = Guid.NewGuid();
        Guid otherOrganization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        QuotaSeed seed = await SeedQuotaRowsAsync(ownerConnection, organization, otherOrganization, actor);

        await using (NpgsqlConnection platform = QuotaRuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.PlatformRole, PostgresRuntimeRoleFixture.PlatformPassword))
        {
            await platform.OpenAsync();
            await using NpgsqlTransaction transaction = await platform.BeginTransactionAsync();
            await SetQuotaScopeAsync(platform, transaction, organization, actor);
            await using NpgsqlCommand update = new("""
                UPDATE app.organization_entitlements SET "Mode" = 2, "Limit" = NULL,
                    "Version" = @version, "SourceRevision" = 2, "SourceHash" = @hash
                WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource
                """, platform, transaction);
            update.Parameters.AddWithValue("version", Guid.NewGuid());
            update.Parameters.AddWithValue("hash", new string('d', 64));
            update.Parameters.AddWithValue("organization", organization);
            update.Parameters.AddWithValue("resource", QuotaResourceKey);
            (await update.ExecuteNonQueryAsync()).ShouldBe(1);
            await transaction.CommitAsync();
        }

        await using NpgsqlConnection runtime = QuotaRuntimeConnection(ownerConnection,
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        await runtime.OpenAsync();
        foreach (Guid target in new[] { organization, otherOrganization })
        {
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetQuotaScopeAsync(runtime, transaction, target, actor);
            await using (NpgsqlCommand reservation = new("""
                UPDATE app.organization_quota_reservations SET "State" = 1, "ClosedAtUtc" = CURRENT_TIMESTAMP
                WHERE "OrganizationId" = @organization AND "Id" = @reservation
                """, runtime, transaction))
            {
                reservation.Parameters.AddWithValue("organization", target);
                reservation.Parameters.AddWithValue("reservation", seed.ReservationIds[target]);
                (await reservation.ExecuteNonQueryAsync()).ShouldBe(1);
            }

            // Cancellation requires fencing first; the other organization proves direct completion.
            int[] states = target == organization ? [2, 3, 3] : [1, 1];
            foreach (int state in states)
            {
                await using NpgsqlCommand upload = new("""
                    UPDATE app.module_upload_operations SET "State" = @state,
                        "ClosedAtUtc" = CASE WHEN @state IN (1, 3)
                            THEN COALESCE("ClosedAtUtc", CURRENT_TIMESTAMP) ELSE NULL END
                    WHERE "OrganizationId" = @organization AND "ResourceKey" = @resource AND "OperationId" = @operation
                    """, runtime, transaction);
                upload.Parameters.AddWithValue("state", state);
                upload.Parameters.AddWithValue("organization", target);
                upload.Parameters.AddWithValue("resource", QuotaResourceKey);
                upload.Parameters.AddWithValue("operation", seed.AdmittedUploadOperationIds[target]);
                (await upload.ExecuteNonQueryAsync()).ShouldBe(1);
            }

            await transaction.CommitAsync();
        }
    }

    private static async Task<PostgreSqlContainer> MigratedQuotaDatabaseAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
        var migration = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        migration.Exit.ShouldBe(0, migration.Error + migration.Output);
        return postgres;
    }

    private static NpgsqlConnection QuotaRuntimeConnection(string ownerConnection, string role, string password) =>
        new(new NpgsqlConnectionStringBuilder(ownerConnection) { Username = role, Password = password }.ConnectionString);

    private static async Task SetQuotaScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? organization,
        Guid? actor)
    {
        await using NpgsqlCommand command = new("""
            SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", organization?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<QuotaSeed> SeedQuotaRowsAsync(
        string ownerConnection,
        Guid organization,
        Guid otherOrganization,
        Guid actor)
    {
        Guid[] organizations = [organization, otherOrganization];
        Dictionary<Guid, Guid> reservationIds = [];
        Dictionary<Guid, Guid> closedReservationIds = [];
        Dictionary<Guid, Guid> usageIds = [];
        Dictionary<Guid, Guid> uploadOperationIds = [];
        Dictionary<Guid, Guid> admittedUploadOperationIds = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();

        foreach (Guid currentOrganization in organizations)
        {
            Guid entitlementVersion = Guid.NewGuid();
            Guid reservationId = Guid.NewGuid();
            Guid closedReservationId = Guid.NewGuid();
            Guid operationId = Guid.NewGuid();
            Guid closedOperationId = Guid.NewGuid();
            Guid usageId = Guid.NewGuid();
            Guid admittedUploadOperationId = Guid.NewGuid();
            Guid fencingUploadOperationId = Guid.NewGuid();
            reservationIds[currentOrganization] = reservationId;
            closedReservationIds[currentOrganization] = closedReservationId;
            usageIds[currentOrganization] = usageId;
            admittedUploadOperationIds[currentOrganization] = admittedUploadOperationId;
            uploadOperationIds[currentOrganization] = fencingUploadOperationId;

            await using (NpgsqlCommand entitlement = new("""
                INSERT INTO app.organization_entitlements
                    ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version",
                     "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
                VALUES (@organization, @resource, 'documents', 1, 1, 1000000, @version,
                        'integration-test', 1, @hash, @now)
                """, owner))
            {
                entitlement.Parameters.AddWithValue("organization", currentOrganization);
                entitlement.Parameters.AddWithValue("resource", QuotaResourceKey);
                entitlement.Parameters.AddWithValue("version", entitlementVersion);
                entitlement.Parameters.AddWithValue("hash", new string('a', 64));
                entitlement.Parameters.AddWithValue("now", now);
                await entitlement.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand reservation = new("""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                     "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                VALUES (@organization, @id, @resource, @actor, 10, @operation, @version,
                        2, 0, @created, @expires, NULL),
                       (@organization, @closedId, @resource, @actor, 10, @closedOperation, @version,
                        2, 1, @created, @expires, @closed)
                """, owner))
            {
                reservation.Parameters.AddWithValue("organization", currentOrganization);
                reservation.Parameters.AddWithValue("id", reservationId);
                reservation.Parameters.AddWithValue("closedId", closedReservationId);
                reservation.Parameters.AddWithValue("resource", QuotaResourceKey);
                reservation.Parameters.AddWithValue("actor", actor);
                reservation.Parameters.AddWithValue("operation", operationId);
                reservation.Parameters.AddWithValue("closedOperation", closedOperationId);
                reservation.Parameters.AddWithValue("version", entitlementVersion);
                reservation.Parameters.AddWithValue("created", now);
                reservation.Parameters.AddWithValue("expires", now.AddHours(1));
                reservation.Parameters.AddWithValue("closed", now.AddMinutes(1));
                await reservation.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand usage = new("""
                INSERT INTO app.organization_usage
                    ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
                VALUES (@organization, @resource, @effect, @source, NULL, @reservation, @actor, 10, @now)
                """, owner))
            {
                usage.Parameters.AddWithValue("organization", currentOrganization);
                usage.Parameters.AddWithValue("resource", QuotaResourceKey);
                usage.Parameters.AddWithValue("effect", usageId);
                usage.Parameters.AddWithValue("source", Guid.NewGuid());
                usage.Parameters.AddWithValue("reservation", reservationId);
                usage.Parameters.AddWithValue("actor", actor);
                usage.Parameters.AddWithValue("now", now);
                await usage.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand inventory = new("""
                INSERT INTO app.organization_quota_inventory
                    ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
                VALUES (@organization, @resource, 'documents.inventory', 1, 'cursor', 1, @now)
                """, owner))
            {
                inventory.Parameters.AddWithValue("organization", currentOrganization);
                inventory.Parameters.AddWithValue("resource", QuotaResourceKey);
                inventory.Parameters.AddWithValue("now", now);
                await inventory.ExecuteNonQueryAsync();
            }

            await InsertQuotaUploadAsync(owner, currentOrganization, actor, reservationId, admittedUploadOperationId,
                Guid.NewGuid(), now, state: 0, closedAt: null, fingerprint: new string('a', 64));
            await InsertQuotaUploadAsync(owner, currentOrganization, actor, closedReservationId, fencingUploadOperationId,
                Guid.NewGuid(), now, state: 2, closedAt: null, fingerprint: new string('a', 64));
        }

        return new QuotaSeed(reservationIds, closedReservationIds, usageIds, uploadOperationIds, admittedUploadOperationIds);
    }

    private static async Task InsertQuotaUploadAsync(
        NpgsqlConnection owner,
        Guid organization,
        Guid actor,
        Guid reservation,
        Guid operation,
        Guid subject,
        DateTimeOffset now,
        int state,
        DateTimeOffset? closedAt,
        string fingerprint)
    {
        await using NpgsqlCommand upload = new("""
            INSERT INTO app.module_upload_operations
                ("OrganizationId", "ResourceKey", "OperationId", "ActorId", "MembershipId", "ReservationId", "SubjectId",
                 "ObjectKey", "StorageNamespaceFingerprint", "Length", "Sha256", "MetadataFingerprint", "State",
                 "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            VALUES (@organization, @resource, @operation, @actor, @membership, @reservation, @subject,
                    @object, @namespace, 1, @sha, @metadata, @state, @created, @expires, @closed)
            """, owner);
        upload.Parameters.AddWithValue("organization", organization);
        upload.Parameters.AddWithValue("resource", QuotaResourceKey);
        upload.Parameters.AddWithValue("operation", operation);
        upload.Parameters.AddWithValue("actor", actor);
        upload.Parameters.AddWithValue("membership", Guid.NewGuid());
        upload.Parameters.AddWithValue("reservation", reservation);
        upload.Parameters.AddWithValue("subject", subject);
        upload.Parameters.AddWithValue("object", $"object-{operation:N}");
        upload.Parameters.AddWithValue("namespace", fingerprint);
        upload.Parameters.AddWithValue("sha", new string('b', 64));
        upload.Parameters.AddWithValue("metadata", fingerprint);
        upload.Parameters.AddWithValue("state", state);
        upload.Parameters.AddWithValue("created", now);
        upload.Parameters.AddWithValue("expires", now.AddHours(1));
        upload.Parameters.AddWithValue("closed", (object?)closedAt ?? DBNull.Value);
        await upload.ExecuteNonQueryAsync();
    }

    private static async Task AssertQuotaRoleDeniedAsync(
        string ownerConnection,
        string role,
        string password,
        Guid organization,
        Guid? actor,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = QuotaRuntimeConnection(ownerConnection, role, password);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetQuotaScopeAsync(connection, transaction, organization, actor);
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        PostgresException denied = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        await transaction.RollbackAsync();
    }

    private static async Task AssertQuotaTransitionRejectedAsync(
        string ownerConnection,
        string role,
        string password,
        Guid organization,
        Guid actor,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = QuotaRuntimeConnection(ownerConnection, role, password);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetQuotaScopeAsync(connection, transaction, organization, actor);
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        PostgresException rejected = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        rejected.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await transaction.RollbackAsync();
    }

    private sealed record QuotaSeed(
        Dictionary<Guid, Guid> ReservationIds,
        Dictionary<Guid, Guid> ClosedReservationIds,
        Dictionary<Guid, Guid> UsageIds,
        Dictionary<Guid, Guid> UploadOperationIds,
        Dictionary<Guid, Guid> AdmittedUploadOperationIds);
}
