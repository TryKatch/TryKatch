using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string QuotaMaintenanceResource = "documents.storage-bytes";

    [TestMethod]
    public async Task FutureMaintenanceUsesDueTimeRatherThanCreationTimeForBacklogAge()
    {
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceBacklog before = await store.ReadBacklogAsync(CancellationToken.None);
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        DateTimeOffset future = new(DateTimeOffset.UtcNow.AddDays(1).UtcTicks / 10 * 10, TimeSpan.Zero);
        await fixture.SeedQuotaMaintenanceWorkAsync(organizationId, dueAt: future, createdAt: DateTimeOffset.UtcNow.AddYears(-1));
        QuotaMaintenanceBacklog after = await store.ReadBacklogAsync(CancellationToken.None);
        DateTimeOffset expected = before.Oldest is { } prior && prior < future ? prior : future;
        after.Oldest.ShouldBe(expected);
    }

    [TestMethod]
    public async Task MaintenanceBacklogReportsQueuedRetryingAndPausedRows()
    {
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceBacklog before = await store.ReadBacklogAsync(CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            Guid organizationId = await fixture.CreateOrganizationAsync();
            await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
            await fixture.SeedQuotaMaintenanceWorkAsync(organizationId,
                state: index == 2 ? 2 : 0, attempts: index == 1 ? 6 : 0);
        }
        QuotaMaintenanceBacklog after = await store.ReadBacklogAsync(CancellationToken.None);
        after.Pending.ShouldBe(before.Pending + 3);
        after.Retrying.ShouldBe(before.Retrying + 1);
        after.Paused.ShouldBe(before.Paused + 1);
        after.Oldest.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task CompetingCoordinatorStoresClaimOneDueWorkItem()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        Guid workId = await fixture.SeedQuotaMaintenanceWorkAsync(organizationId);
        QuotaMaintenanceStore first = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceStore second = fixture.CreateQuotaMaintenanceStore();

        QuotaMaintenanceClaim?[] claims = await Task.WhenAll(
            first.ClaimAsync(workId, CancellationToken.None),
            second.ClaimAsync(workId, CancellationToken.None));

        claims.Count(claim => claim is not null).ShouldBe(1);
        QuotaMaintenanceClaim winner = claims.Single(claim => claim is not null)!;
        winner.Work.Id.ShouldBe(workId);
        winner.LeaseId.ShouldNotBe(Guid.Empty);
        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(1);
    }

    [TestMethod]
    public async Task FutureDueActiveLeaseAndCompletedRowsAreNotClaimed()
    {
        Guid futureOrganization = await fixture.CreateOrganizationAsync();
        Guid leasedOrganization = await fixture.CreateOrganizationAsync();
        Guid completedOrganization = await fixture.CreateOrganizationAsync();
        foreach (Guid organizationId in new[] { futureOrganization, leasedOrganization, completedOrganization })
            await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);

        Guid future = await fixture.SeedQuotaMaintenanceWorkAsync(futureOrganization,
            dueAt: DateTimeOffset.UtcNow.AddMinutes(5));
        Guid activeLease = await fixture.SeedQuotaMaintenanceWorkAsync(leasedOrganization,
            state: 1, attempts: 1, leaseId: Guid.NewGuid(), leaseUntil: DateTimeOffset.UtcNow.AddMinutes(2));
        Guid completed = await fixture.SeedQuotaMaintenanceWorkAsync(completedOrganization, state: 3);
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();

        (await store.ClaimAsync(future, CancellationToken.None)).ShouldBeNull();
        (await store.ClaimAsync(activeLease, CancellationToken.None)).ShouldBeNull();
        (await store.ClaimAsync(completed, CancellationToken.None)).ShouldBeNull();
        (await fixture.ReadQuotaMaintenanceWorkAsync(future)).State.ShouldBe(0);
        (await fixture.ReadQuotaMaintenanceWorkAsync(activeLease)).State.ShouldBe(1);
        (await fixture.ReadQuotaMaintenanceWorkAsync(completed)).State.ShouldBe(3);
    }

    [TestMethod]
    public async Task ExpiredLeaseCanBeReclaimedButOldLeaseCannotFinishOrReleaseNewLease()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        Guid workId = await fixture.SeedQuotaMaintenanceWorkAsync(organizationId);
        QuotaMaintenanceStore originalStore = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceClaim original = (await originalStore.ClaimAsync(workId, CancellationToken.None))!;
        await fixture.ExpireQuotaMaintenanceLeaseAsync(workId);

        (await originalStore.FinishAsync(original,
            new(QuotaMaintenanceExecutionState.Completed), CancellationToken.None)).ShouldBeFalse();
        await originalStore.ReleaseAsync(original, CancellationToken.None);

        QuotaMaintenanceStore restartedStore = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceClaim reclaimed = (await restartedStore.ClaimAsync(workId, CancellationToken.None))!;
        reclaimed.LeaseId.ShouldNotBe(original.LeaseId);

        (await originalStore.FinishAsync(original,
            new(QuotaMaintenanceExecutionState.Completed), CancellationToken.None)).ShouldBeFalse();
        await originalStore.ReleaseAsync(original, CancellationToken.None);
        QuotaMaintenanceWorkSnapshot afterStaleOperations = await fixture.ReadQuotaMaintenanceWorkAsync(workId);
        afterStaleOperations.State.ShouldBe(1);
        afterStaleOperations.LeaseId.ShouldBe(reclaimed.LeaseId);

        (await restartedStore.FinishAsync(reclaimed,
            new(QuotaMaintenanceExecutionState.Completed), CancellationToken.None)).ShouldBeTrue();
        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(3);
    }

    [TestMethod]
    public async Task FinishCannotUseClaimForAnotherOrganization()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        Guid otherOrganizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        await fixture.SeedQuotaProjectionAsync(otherOrganizationId, limit: 100);
        Guid workId = await fixture.SeedQuotaMaintenanceWorkAsync(organizationId);
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();
        QuotaMaintenanceClaim claim = (await store.ClaimAsync(workId, CancellationToken.None))!;
        QuotaMaintenanceWorkRecord forgedWork = new()
        {
            OrganizationId = otherOrganizationId,
            Id = claim.Work.Id,
            Kind = claim.Work.Kind,
            ResourceKey = claim.Work.ResourceKey,
            ReservationId = claim.Work.ReservationId,
            UploadOperationId = claim.Work.UploadOperationId,
            State = claim.Work.State,
            DueAtUtc = claim.Work.DueAtUtc,
            CreatedAtUtc = claim.Work.CreatedAtUtc,
            Attempts = claim.Work.Attempts,
            LeaseId = claim.Work.LeaseId,
            LeaseUntilUtc = claim.Work.LeaseUntilUtc,
            LastFailureCode = claim.Work.LastFailureCode
        };

        (await store.FinishAsync(claim with { Work = forgedWork },
            new(QuotaMaintenanceExecutionState.Completed), CancellationToken.None)).ShouldBeFalse();
        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(1);
    }

    [TestMethod]
    public async Task UnavailableWorkAfterSixAttemptsStaysRetryableWithoutTerminalCutoff()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        Guid workId = await fixture.SeedQuotaMaintenanceWorkAsync(organizationId, attempts: 6);
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();

        QuotaMaintenanceClaim claim = (await store.ClaimAsync(workId, CancellationToken.None))!;
        claim.Work.Attempts.ShouldBe(7);
        (await store.FinishAsync(claim,
            new(QuotaMaintenanceExecutionState.Unavailable, "storage_unavailable"), CancellationToken.None)).ShouldBeTrue();

        QuotaMaintenanceWorkSnapshot retry = await fixture.ReadQuotaMaintenanceWorkAsync(workId);
        retry.State.ShouldBe(0);
        retry.Attempts.ShouldBe(7);
        retry.LastFailureCode.ShouldBe("storage_unavailable");
        DateTimeOffset databaseNow = await fixture.ReadDatabaseUtcNowAsync();
        (retry.DueAtUtc - databaseNow).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        (retry.DueAtUtc - databaseNow).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        (await store.ClaimAsync(workId, CancellationToken.None)).ShouldBeNull();

        await fixture.MakeQuotaMaintenanceWorkDueAsync(workId);
        QuotaMaintenanceClaim retryClaim = (await store.ClaimAsync(workId, CancellationToken.None))!;
        retryClaim.Work.Attempts.ShouldBe(8);
    }

    [TestMethod]
    public async Task AttemptsSaturateAtIntMaximumAndCompletedWorkNeverReturnsToQueue()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedQuotaProjectionAsync(organizationId, limit: 100);
        Guid workId = await fixture.SeedQuotaMaintenanceWorkAsync(organizationId, attempts: int.MaxValue);
        QuotaMaintenanceStore store = fixture.CreateQuotaMaintenanceStore();

        QuotaMaintenanceClaim claim = (await store.ClaimAsync(workId, CancellationToken.None))!;
        claim.Work.Attempts.ShouldBe(int.MaxValue);
        (await store.FinishAsync(claim,
            new(QuotaMaintenanceExecutionState.Completed), CancellationToken.None)).ShouldBeTrue();

        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(3);
        (await store.ClaimAsync(workId, CancellationToken.None)).ShouldBeNull();
    }

    [TestMethod]
    public async Task TransactionOnlyReservationAndExpiryWorkAreInsertedAndRolledBackTogether()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100);
        Guid committedOperation = Guid.NewGuid();
        Guid committedReservationId;
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult result = await ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services)
                .ReserveAsync(QuotaMaintenanceResource, 1, committedOperation);
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            committedReservationId = result.Reservation!.Id;
            await scope.CommitAsync();
        }

        QuotaMaintenanceWorkSnapshot committedWork = await fixture.ReadQuotaMaintenanceReservationWorkAsync(
            user.OrganizationId, committedReservationId);
        committedWork.Kind.ShouldBe(1);
        committedWork.ReservationId.ShouldBe(committedReservationId);
        committedWork.UploadOperationId.ShouldBeNull();
        committedWork.ReservationExpiresAtUtc.ShouldNotBeNull();
        committedWork.DueAtUtc.ShouldBe(committedWork.ReservationExpiresAtUtc.Value);

        Guid rolledBackOperation = Guid.NewGuid();
        Guid rolledBackReservationId;
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult result = await ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services)
                .ReserveAsync(QuotaMaintenanceResource, 1, rolledBackOperation);
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            rolledBackReservationId = result.Reservation!.Id;
        }

        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, rolledBackOperation)).ShouldBe(0);
        (await fixture.CountQuotaMaintenanceReservationWorkAsync(user.OrganizationId, rolledBackReservationId)).ShouldBe(0);
    }

    private sealed record QuotaMaintenanceWorkSnapshot(
        int State,
        int Attempts,
        Guid? LeaseId,
        DateTimeOffset DueAtUtc,
        DateTimeOffset? LeaseUntilUtc,
        string? LastFailureCode,
        int Kind = 3,
        Guid? ReservationId = null,
        Guid? UploadOperationId = null,
        DateTimeOffset? ReservationExpiresAtUtc = null);

    private sealed partial class ExecutionFixture
    {
        public QuotaMaintenanceStore CreateQuotaMaintenanceStore() =>
            new(services.GetRequiredService<IConfiguration>());

        public Task<Guid> SeedQuotaMaintenanceWorkAsync(Guid organizationId,
            DateTimeOffset? dueAt = null, int state = 0, int attempts = 0,
            Guid? leaseId = null, DateTimeOffset? leaseUntil = null, DateTimeOffset? createdAt = null) => SeedQuotaMaintenanceWorkCoreAsync(
                organizationId, dueAt, state, attempts, leaseId, leaseUntil, createdAt);

        private async Task<Guid> SeedQuotaMaintenanceWorkCoreAsync(Guid organizationId,
            DateTimeOffset? dueAt, int state, int attempts, Guid? leaseId, DateTimeOffset? leaseUntil, DateTimeOffset? createdAt)
        {
            Guid id = Guid.CreateVersion7();
            DateTimeOffset due = dueAt ?? DateTimeOffset.UtcNow.AddMinutes(-1);
            DateTimeOffset created = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-2);
            await ExecuteOwnerAsync("""
                INSERT INTO infrastructure.quota_maintenance_work
                    ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId",
                     "State", "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
                VALUES (@organization, @id, 3, 'documents.storage-bytes', NULL, NULL,
                    @state, @due, @created, @attempts, @lease, @leaseUntil, NULL)
                """, ("organization", organizationId), ("id", id), ("state", state), ("due", due),
                ("created", created), ("attempts", attempts), ("lease", (object?)leaseId ?? DBNull.Value),
                ("leaseUntil", (object?)leaseUntil ?? DBNull.Value));
            return id;
        }

        public Task ExpireQuotaMaintenanceLeaseAsync(Guid id) => ExecuteOwnerAsync("""
            UPDATE infrastructure.quota_maintenance_work
            SET "LeaseUntilUtc" = CURRENT_TIMESTAMP - INTERVAL '1 second'
            WHERE "Id" = @id
            """, ("id", id));

        public Task MakeQuotaMaintenanceWorkDueAsync(Guid id) => ExecuteOwnerAsync("""
            UPDATE infrastructure.quota_maintenance_work SET "DueAtUtc" = CURRENT_TIMESTAMP - INTERVAL '1 second'
            WHERE "Id" = @id
            """, ("id", id));

        public async Task<DateTimeOffset> ReadDatabaseUtcNowAsync()
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT CURRENT_TIMESTAMP", connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return reader.GetFieldValue<DateTimeOffset>(0);
        }

        public async Task<QuotaMaintenanceWorkSnapshot> ReadQuotaMaintenanceWorkAsync(Guid id)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT work."State", work."Attempts", work."LeaseId", work."DueAtUtc", work."LeaseUntilUtc",
                       work."LastFailureCode", work."Kind", work."ReservationId", work."UploadOperationId",
                       reservation."ExpiresAtUtc"
                FROM infrastructure.quota_maintenance_work work
                LEFT JOIN app.organization_quota_reservations reservation
                  ON reservation."OrganizationId" = work."OrganizationId" AND reservation."Id" = work."ReservationId"
                WHERE work."Id" = @id
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return new(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
        }

        public Task<QuotaMaintenanceWorkSnapshot> ReadQuotaMaintenanceReservationWorkAsync(Guid organizationId,
            Guid reservationId) => ReadQuotaMaintenanceReservationWorkCoreAsync(organizationId, reservationId);

        private async Task<QuotaMaintenanceWorkSnapshot> ReadQuotaMaintenanceReservationWorkCoreAsync(Guid organizationId,
            Guid reservationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT work."State", work."Attempts", work."LeaseId", work."DueAtUtc", work."LeaseUntilUtc",
                       work."LastFailureCode", work."Kind", work."ReservationId", work."UploadOperationId",
                       reservation."ExpiresAtUtc"
                FROM infrastructure.quota_maintenance_work work
                JOIN app.organization_quota_reservations reservation
                  ON reservation."OrganizationId" = work."OrganizationId" AND reservation."Id" = work."ReservationId"
                WHERE work."OrganizationId" = @organization AND work."ReservationId" = @reservation
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("reservation", reservationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return new(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
        }

        public Task<long> CountQuotaMaintenanceReservationWorkAsync(Guid organizationId, Guid reservationId) => CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.quota_maintenance_work
            WHERE "OrganizationId" = @organization AND "ReservationId" = @reservation
            """, ("organization", organizationId), ("reservation", reservationId));
    }
}
