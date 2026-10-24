using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task QuotaBoundaryCommitAndUsageAreRolledBackWithTheOuterTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);

        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaService quota = CreateQuota(scope);
            QuotaReservationResult reservation = await quota.ReserveAsync(DocumentsQuotaResource, 3, Guid.NewGuid());
            reservation.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await quota.CommitAsync(reservation.Reservation!.Id);
            // Disposing without committing rolls back both the terminal transition and its ledger effect.
        }

        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryPublicTransitionsRejectUploadHoldsWithoutUploadRecords()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        Guid commitId = await fixture.SeedUploadKindHoldAsync(user);
        Guid releaseId = await fixture.SeedUploadKindHoldAsync(user);

        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaService quota = CreateQuota(scope);
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.CommitAsync(commitId));
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.ReleaseAsync(releaseId));
            await scope.CommitAsync();
        }

        (await fixture.CountUploadRecordsForOrganizationAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, commitId)).ShouldBe(0);
        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, releaseId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryOtherValidActorCannotCommitOrReleaseAnotherActorsHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        Guid reservationId;
        await using (MutationScope ownerScope = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult reservation = await CreateQuota(ownerScope)
                .ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());
            reservation.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            reservationId = reservation.Reservation!.Id;
            await ownerScope.CommitAsync();
        }

        ProjectsSeededUser otherActor = await fixture.SeedAdditionalProjectsActorAsync(user.OrganizationId);
        await using (MutationScope otherScope = await fixture.BeginMutationScopeAsync(otherActor))
        {
            OrganizationQuotaService quota = CreateQuota(otherScope);
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.CommitAsync(reservationId));
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.ReleaseAsync(reservationId));
            await otherScope.CommitAsync();
        }

        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, reservationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    [DataRow("organization")]
    [DataRow("actor")]
    public async Task QuotaBoundaryEnlistedScopeMismatchThrowsBeforeAnyWrite(string setting)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await ExecutionFixture.SetEnlistedQuotaSettingAsync(scope, setting, Guid.NewGuid());

        await Should.ThrowAsync<InvalidOperationException>(() => CreateQuota(scope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid()));
        await scope.CommitAsync();

        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryCanceledCallerTokenPropagatesWithoutCreatingHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => CreateQuota(scope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid(), cancellation.Token));
        await scope.CommitAsync();

        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryDatabaseTerminationReturnsUnavailableWithoutCreatingHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult? result = null;
        try
        {
            int backendPid = await ExecutionFixture.GetQuotaApplicationBackendPidAsync(scope);
            await fixture.TerminateBackendAsync(backendPid);
            result = await CreateQuota(scope).ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        }
        finally
        {
            // The transaction belongs to the terminated backend; disposal may report that lost connection.
            try { await scope.DisposeAsync(); }
            catch (NpgsqlException) { }
            catch (InvalidOperationException) { }
        }

        result.ShouldNotBeNull();
        result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
        result.Reservation.ShouldBeNull();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryRevokedMembershipIsDeniedBeforeBusyHoldDisclosure()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using MutationScope firstScope = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult first = await CreateQuota(firstScope)
            .ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());
        first.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);

        await using MutationScope staleScope = await fixture.BeginMutationScopeAsync(user);
        (await fixture.RevokeMembershipAsync(user.OrganizationId, user.ActorId)).ShouldBe(1);
        QuotaReservationResult stale = await CreateQuota(staleScope)
            .ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());

        stale.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        stale.FailureCode.ShouldBe("quota_authority_denied");
        stale.Reservation.ShouldBeNull();
        await staleScope.CommitAsync();
        await firstScope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaBoundaryLedgerInsertFailureLeavesPersistedHoldReserved()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        Guid reservationId;
        await using (MutationScope reserveScope = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult reservation = await CreateQuota(reserveScope)
                .ReserveAsync(DocumentsQuotaResource, 3, Guid.NewGuid());
            reservation.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            reservationId = reservation.Reservation!.Id;
            await reserveScope.CommitAsync();
        }

        string constraint = $"ck_quota_boundary_usage_{Guid.NewGuid():N}";
        await fixture.RejectQuotaUsageForOrganizationAsync(constraint, user.OrganizationId);
        try
        {
            await using MutationScope commitScope = await fixture.BeginMutationScopeAsync(user);
            await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreateQuota(commitScope)
                .CommitAsync(reservationId));
        }
        finally
        {
            await fixture.DropQuotaUsageConstraintAsync(constraint);
        }

        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, reservationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaBoundaryDatabaseAggregateOverflowNeverAdmits()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: long.MaxValue);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, long.MaxValue);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, 1);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult result = await CreateQuota(scope)
                .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
            result.FailureCode.ShouldBe("quota_usage_invalid");
            result.Reservation.ShouldBeNull();
        }
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    [DataRow(System.Data.IsolationLevel.RepeatableRead)]
    [DataRow(System.Data.IsolationLevel.Serializable)]
    public async Task QuotaBoundarySnapshotTransactionsCannotUseStaleCapacity(System.Data.IsolationLevel isolation)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using AsyncServiceScope scope = fixture.CreateScope();
        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using IDbContextTransaction transaction = await control.Database.BeginTransactionAsync(isolation);
        await using IDbContextTransaction enlisted = await OrganizationTransactionEnlistment.EnlistAsync(
            control, application, user.OrganizationId, user.ActorId, CancellationToken.None);

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.ServiceProvider)
                .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid()));
        failure.Message.ShouldContain("ReadCommitted");
    }

    [TestMethod]
    public async Task QuotaBoundarySeparateReadCommittedTransactionsAreNotAnEnlistedScope()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await using ApplicationDbContext separate = fixture.CreateSeparateQuotaApplicationContext();
        await using IDbContextTransaction transaction = await separate.Database.BeginTransactionAsync();

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services, separate)
                .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid()));
        failure.Message.ShouldContain("same active ReadCommitted");
    }

    [TestMethod]
    public async Task QuotaBoundarySignedLedgerEffectsAggregateOnceBeforeAddingOutstandingHolds()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await fixture.SeedSignedQuotaHistoryAsync(user.OrganizationId);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService quota = CreateQuota(scope);
        Guid operationId = Guid.NewGuid();

        QuotaReservationResult reserved = await quota.ReserveAsync(DocumentsQuotaResource, 4, operationId);
        reserved.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        QuotaReservationResult retry = await quota.ReserveAsync(DocumentsQuotaResource, 4, operationId);
        retry.Disposition.ShouldBe(QuotaReservationDisposition.AlreadyReserved);
        retry.Reservation.ShouldBe(reserved.Reservation);
        QuotaReservationResult exhausted = await quota.ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        exhausted.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        exhausted.FailureCode.ShouldBe("quota_exhausted");
        await scope.CommitAsync();

        (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(6);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(2);
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(1);
    }

    private sealed partial class ExecutionFixture
    {
        public ApplicationDbContext CreateSeparateQuotaApplicationContext() => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(organizationConnection).Options);

        public async Task SeedSignedQuotaHistoryAsync(Guid organizationId)
        {
            // Existing history setup only; the closed removal policy is implemented in Task5.
            Guid source = Guid.NewGuid();
            Guid positive = Guid.NewGuid();
            Guid negative = Guid.NewGuid();
            Guid actor = Guid.NewGuid();
            const string insert = """
                INSERT INTO app.organization_usage
                    ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
                VALUES (@organization, 'documents.storage-bytes', @positive, @source, NULL, NULL, @actor, 10, CURRENT_TIMESTAMP),
                       (@organization, 'documents.storage-bytes', @negative, @source, @positive, NULL, @actor, -4, CURRENT_TIMESTAMP)
                """;
            await ExecuteOwnerAsync(insert, ("organization", organizationId), ("positive", positive),
                ("negative", negative), ("source", source), ("actor", actor));
            PostgresException duplicate = await Should.ThrowAsync<PostgresException>(() => ExecuteOwnerAsync(insert,
                ("organization", organizationId), ("positive", positive), ("negative", negative), ("source", source), ("actor", actor)));
            duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }

        public async Task<Guid> SeedUploadKindHoldAsync(ProjectsSeededUser user)
        {
            Guid reservationId = Guid.CreateVersion7();
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                     "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                SELECT @organization, @id, 'documents.storage-bytes', @actor, 1, @operation, "Version",
                       2, 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + INTERVAL '15 minutes', NULL
                FROM app.organization_entitlements
                WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
                """, ("organization", user.OrganizationId), ("id", reservationId), ("actor", user.ActorId),
                ("operation", Guid.CreateVersion7()));
            return reservationId;
        }

        public Task<long> CountUploadRecordsForOrganizationAsync(Guid organizationId) => CountOwnerAsync(
            "SELECT count(*) FROM app.module_upload_operations WHERE \"OrganizationId\"=@organization",
            ("organization", organizationId));

        public Task<long> ReadQuotaReservationStateAsync(Guid organizationId, Guid reservationId) => CountOwnerAsync("""
            SELECT "State"::bigint FROM app.organization_quota_reservations
            WHERE "OrganizationId"=@organization AND "Id"=@reservation
            """, ("organization", organizationId), ("reservation", reservationId));

        public static async Task SetEnlistedQuotaSettingAsync(MutationScope scope, string setting, Guid value)
        {
            string key = setting switch
            {
                "organization" => "app.organization_id",
                "actor" => "app.actor_id",
                _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown organization setting.")
            };
            ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
            NpgsqlConnection connection = (NpgsqlConnection)application.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using NpgsqlCommand command = new($"SELECT set_config('{key}', @value, true)", connection,
                (NpgsqlTransaction)application.Database.CurrentTransaction!.GetDbTransaction());
            command.Parameters.AddWithValue("value", value.ToString());
            await command.ExecuteScalarAsync();
        }

        public static async Task<int> GetQuotaApplicationBackendPidAsync(MutationScope scope)
        {
            ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
            await using NpgsqlCommand command = new("SELECT pg_backend_pid()", (NpgsqlConnection)application.Database.GetDbConnection(),
                (NpgsqlTransaction)application.Database.CurrentTransaction!.GetDbTransaction());
            return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task TerminateBackendAsync(int backendPid)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT pg_terminate_backend(@pid)", connection);
            command.Parameters.AddWithValue("pid", backendPid);
            (await command.ExecuteScalarAsync()).ShouldBe(true);
        }

        public Task RejectQuotaUsageForOrganizationAsync(string constraint, Guid organizationId) => ExecuteOwnerAsync($"""
            ALTER TABLE app.organization_usage
            ADD CONSTRAINT {constraint} CHECK ("OrganizationId" <> '{organizationId:D}'::uuid)
            """);

        public Task DropQuotaUsageConstraintAsync(string constraint) => ExecuteOwnerAsync(
            $"ALTER TABLE app.organization_usage DROP CONSTRAINT IF EXISTS {constraint}");
    }
}
