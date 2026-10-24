using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string DocumentsQuotaResource = "documents.storage-bytes";

    [TestMethod]
    public async Task QuotaEngineReadsOnlyTheCurrentOrganizationsDurableEntitlement()
    {
        ProjectsSeededUser first = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser second = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(first.OrganizationId, limit: 10);
        await fixture.SeedQuotaProjectionAsync(second.OrganizationId, limit: 20);
        await using MutationScope firstScope = await fixture.BeginMutationScopeAsync(first);
        await using MutationScope secondScope = await fixture.BeginMutationScopeAsync(second);

        OrganizationEntitlement firstProjection = await CreateQuota(firstScope).GetAsync(DocumentsQuotaResource);
        OrganizationEntitlement secondProjection = await CreateQuota(secondScope).GetAsync(DocumentsQuotaResource);

        firstProjection.ResourceKey.ShouldBe(DocumentsQuotaResource);
        firstProjection.Mode.ShouldBe(OrganizationEntitlementMode.Finite);
        firstProjection.Limit.ShouldBe(10);
        secondProjection.Limit.ShouldBe(20);
        firstProjection.Version.ShouldNotBe(Guid.Empty);
        secondProjection.Version.ShouldNotBe(firstProjection.Version);
        firstProjection.Source.ShouldBe(HostEntitlementPublisher.Source);
    }

    [TestMethod]
    public async Task QuotaEngineEntitlementReaderDoesNotInventMissingOrReinterpretedAllowances()
    {
        ProjectsSeededUser missing = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(missing))
        {
            (await CreateQuota(scope).GetAsync(DocumentsQuotaResource)).Mode.ShouldBe(OrganizationEntitlementMode.Disabled);
            await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreateQuota(scope).GetAsync("documents.unknown"));
        }
        ProjectsSeededUser mismatch = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(mismatch.OrganizationId, limit: 10, unit: ModuleQuotaUnit.Count);
        await using MutationScope mismatchedScope = await fixture.BeginMutationScopeAsync(mismatch);
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreateQuota(mismatchedScope).GetAsync(DocumentsQuotaResource));
    }

    [TestMethod]
    public async Task QuotaEngineReserveRejectsInvalidRequestsAndUnknownOrUnconfiguredResources()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService quota = CreateQuota(scope);

        QuotaReservationResult emptyOperation = await quota.ReserveAsync(DocumentsQuotaResource, 1, Guid.Empty);
        emptyOperation.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        emptyOperation.FailureCode.ShouldBe("quota_request_invalid");
        emptyOperation.Reservation.ShouldBeNull();

        QuotaReservationResult zeroUnits = await quota.ReserveAsync(DocumentsQuotaResource, 0, Guid.NewGuid());
        zeroUnits.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        zeroUnits.FailureCode.ShouldBe("quota_request_invalid");
        zeroUnits.Reservation.ShouldBeNull();

        QuotaReservationResult negativeUnits = await quota.ReserveAsync(DocumentsQuotaResource, -1, Guid.NewGuid());
        negativeUnits.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        negativeUnits.FailureCode.ShouldBe("quota_request_invalid");
        negativeUnits.Reservation.ShouldBeNull();

        QuotaReservationResult unknown = await quota.ReserveAsync("documents.unknown", 1, Guid.NewGuid());
        unknown.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        unknown.Reservation.ShouldBeNull();

        QuotaReservationResult missingProjection = await quota.ReserveAsync("documents.missing", 1, Guid.NewGuid());
        missingProjection.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        missingProjection.Reservation.ShouldBeNull();
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);

        ProjectsSeededUser unconfiguredUser = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope unconfiguredScope = await fixture.BeginMutationScopeAsync(unconfiguredUser);
        QuotaReservationResult unconfigured = await CreatePublishedQuota(unconfiguredScope, CreatePublication(unconfiguredScope, revision: 1))
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        unconfigured.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        unconfigured.Reservation.ShouldBeNull();
        await unconfiguredScope.CommitAsync();
    }

    [TestMethod]
    public async Task QuotaEngineReserveUsesOneWinnerForTheFiniteFinalSlotAndRechecksAfterCommit()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 1);
        await using MutationScope firstScope = await fixture.BeginMutationScopeAsync(user);
        await using MutationScope secondScope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService firstQuota = CreateQuota(firstScope);
        OrganizationQuotaService secondQuota = CreateQuota(secondScope);
        Guid firstOperation = Guid.NewGuid();
        Guid secondOperation = Guid.NewGuid();
        Task<QuotaReservationResult> firstTask = firstQuota.ReserveAsync(DocumentsQuotaResource, 1, firstOperation);
        Task<QuotaReservationResult> secondTask = secondQuota.ReserveAsync(DocumentsQuotaResource, 1, secondOperation);

        Task<QuotaReservationResult> firstCompleted = await Task.WhenAny(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));
        QuotaReservationResult firstResult = await firstCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        MutationScope winnerScope;
        Task<QuotaReservationResult> loserTask;
        MutationScope loserScope;
        QuotaReservationResult loserResult;
        if (firstResult.Disposition == QuotaReservationDisposition.Reserved)
        {
            firstResult.Reservation.ShouldNotBeNull();
            winnerScope = ReferenceEquals(firstCompleted, firstTask) ? firstScope : secondScope;
            loserTask = ReferenceEquals(firstCompleted, firstTask) ? secondTask : firstTask;
            loserScope = ReferenceEquals(firstCompleted, firstTask) ? secondScope : firstScope;
            await winnerScope.CommitAsync();
            loserResult = await loserTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            firstResult.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
            firstResult.FailureCode.ShouldBe("quota_busy");
            loserResult = firstResult;
            loserScope = ReferenceEquals(firstCompleted, firstTask) ? firstScope : secondScope;
            Task<QuotaReservationResult> winnerTask = ReferenceEquals(firstCompleted, firstTask) ? secondTask : firstTask;
            winnerScope = ReferenceEquals(firstCompleted, firstTask) ? secondScope : firstScope;
            QuotaReservationResult winner = await winnerTask.WaitAsync(TimeSpan.FromSeconds(10));
            winner.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            winner.Reservation.ShouldNotBeNull();
            await winnerScope.CommitAsync();
        }

        if (loserResult.Disposition == QuotaReservationDisposition.Unavailable)
        {
            loserResult.FailureCode.ShouldBe("quota_busy");
            await loserScope.CommitAsync();
        }
        else
        {
            loserResult.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
            loserResult.FailureCode.ShouldBe("quota_exhausted");
            await loserScope.CommitAsync();
        }

        await using MutationScope retryScope = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult retry = await CreateQuota(retryScope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        retry.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        retry.FailureCode.ShouldBe("quota_exhausted");
        retry.Reservation.ShouldBeNull();
        await retryScope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaEngineReserveRetryReturnsTheSameHoldAndChangedRequestConflictsWithoutDisclosure()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 20);
        Guid operationId = Guid.NewGuid();
        await using MutationScope firstScope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService firstQuota = CreateQuota(firstScope);
        QuotaReservationResult reserved = await firstQuota.ReserveAsync(DocumentsQuotaResource, 4, operationId);
        reserved.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        reserved.Reservation.ShouldNotBeNull();

        QuotaReservationResult retry = await firstQuota.ReserveAsync(DocumentsQuotaResource, 4, operationId);
        retry.Disposition.ShouldBe(QuotaReservationDisposition.AlreadyReserved);
        retry.Reservation.ShouldBe(reserved.Reservation);

        QuotaReservationResult changedUnits = await firstQuota.ReserveAsync(DocumentsQuotaResource, 5, operationId);
        changedUnits.Disposition.ShouldBe(QuotaReservationDisposition.Conflict);
        changedUnits.Reservation.ShouldBeNull();
        await firstScope.CommitAsync();

        ProjectsSeededUser otherActor = await fixture.SeedAdditionalProjectsActorAsync(user.OrganizationId);
        await using MutationScope otherScope = await fixture.BeginMutationScopeAsync(otherActor);
        QuotaReservationResult changedActor = await CreateQuota(otherScope)
            .ReserveAsync(DocumentsQuotaResource, 4, operationId);
        changedActor.Disposition.ShouldBe(QuotaReservationDisposition.Conflict);
        changedActor.Reservation.ShouldBeNull();
        await otherScope.CommitAsync();
    }

    [TestMethod]
    public async Task QuotaEngineReservationRollbackLeavesNoAdmittedStateAndCapacityCanBeRetried()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 3);
        Guid operationId = Guid.NewGuid();
        await using (MutationScope rolledBack = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult reserved = await CreateQuota(rolledBack)
                .ReserveAsync(DocumentsQuotaResource, 3, operationId);
            reserved.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        }
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);

        await using MutationScope retry = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult result = await CreateQuota(retry).ReserveAsync(DocumentsQuotaResource, 3, operationId);
        result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        result.Reservation.ShouldNotBeNull();
        await retry.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaEngineCommitAndReleaseAreIdempotentOnlyForTheirOwnTerminalState()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using (MutationScope committed = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaService quota = CreateQuota(committed);
            QuotaReservationResult result = await quota.ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            Guid id = result.Reservation!.Id;
            await quota.CommitAsync(id);
            await quota.CommitAsync(id);
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.ReleaseAsync(id));
            QuotaReservationResult afterCommit = await quota.ReserveAsync(
                DocumentsQuotaResource, 2, result.Reservation.OperationId);
            afterCommit.Disposition.ShouldBe(QuotaReservationDisposition.Conflict);
            afterCommit.Reservation.ShouldBeNull();
            await committed.CommitAsync();
        }

        await using (MutationScope released = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaService quota = CreateQuota(released);
            QuotaReservationResult result = await quota.ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            Guid id = result.Reservation!.Id;
            await quota.ReleaseAsync(id);
            await quota.ReleaseAsync(id);
            await Should.ThrowAsync<OrganizationQuotaConflictException>(() => quota.CommitAsync(id));
            QuotaReservationResult afterRelease = await quota.ReserveAsync(
                DocumentsQuotaResource, 2, result.Reservation.OperationId);
            afterRelease.Disposition.ShouldBe(QuotaReservationDisposition.Conflict);
            afterRelease.Reservation.ShouldBeNull();
            await released.CommitAsync();
        }
    }

    [TestMethod]
    public async Task QuotaEngineCommitChargesOnceAndReleaseDoesNotCharge()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 20);
        Guid committedOperation = Guid.NewGuid();
        Guid releasedOperation = Guid.NewGuid();
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaService quota = CreateQuota(scope);
            QuotaReservationResult committed = await quota.ReserveAsync(DocumentsQuotaResource, 3, committedOperation);
            committed.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await quota.CommitAsync(committed.Reservation!.Id);
            await quota.CommitAsync(committed.Reservation.Id);

            QuotaReservationResult released = await quota.ReserveAsync(DocumentsQuotaResource, 4, releasedOperation);
            released.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await quota.ReleaseAsync(released.Reservation!.Id);
            await scope.CommitAsync();
        }

        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(1);
        (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(3);
        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, committedOperation)).ShouldBe(1);
        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, releasedOperation)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaEngineActiveReservationStillConsumesCapacityAfterItsExpiryTimestamp()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 5);
        await fixture.SeedExpiredActiveReservationAsync(user, units: 5);

        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult result = await CreateQuota(scope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        result.FailureCode.ShouldBe("quota_exhausted");
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();
    }

    [TestMethod]
    public async Task QuotaEngineFiniteInventoryMustBeCompleteButDisabledAndUnlimitedResourcesStillRecordUsage()
    {
        ProjectsSeededUser finiteUser = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(finiteUser.OrganizationId, limit: 5, inventoryComplete: false);
        await using (MutationScope finite = await fixture.BeginMutationScopeAsync(finiteUser))
        {
            QuotaReservationResult result = await CreateQuota(finite)
                .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
            result.FailureCode.ShouldBe("quota_inventory_initializing");
            result.Reservation.ShouldBeNull();
            await finite.CommitAsync();
        }

        foreach (OrganizationEntitlementMode mode in new[]
        {
            OrganizationEntitlementMode.Disabled, OrganizationEntitlementMode.Unlimited
        })
        {
            ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
            await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: null, mode: mode, inventoryComplete: false);
            await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
            OrganizationQuotaService quota = CreateQuota(scope);
            QuotaReservationResult result = await quota.ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await quota.CommitAsync(result.Reservation!.Id);
            await scope.CommitAsync();
            (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(2);
        }
    }

    [TestMethod]
    public async Task QuotaEngineRetainedProjectionUnitMustMatchTheInstalledDocumentsDescriptor()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10, unit: ModuleQuotaUnit.Count);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);

        QuotaReservationResult result = await CreateQuota(scope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaEngineActiveHoldSurvivesDowngradeForRetryWhileNewOperationIsDenied()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        Guid operationId = Guid.NewGuid();
        Guid reservationId;
        await using (MutationScope initial = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult result = await CreateQuota(initial)
                .ReserveAsync(DocumentsQuotaResource, 7, operationId);
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            reservationId = result.Reservation!.Id;
            await initial.CommitAsync();
        }
        await fixture.SetQuotaProjectionAsync(user.OrganizationId, limit: 6);

        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService quota = CreateQuota(scope);
        QuotaReservationResult retry = await quota.ReserveAsync(DocumentsQuotaResource, 7, operationId);
        retry.Disposition.ShouldBe(QuotaReservationDisposition.AlreadyReserved);
        retry.Reservation!.Id.ShouldBe(reservationId);

        QuotaReservationResult newOperation = await quota.ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        newOperation.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        newOperation.FailureCode.ShouldBe("quota_exhausted");
        newOperation.Reservation.ShouldBeNull();
        await scope.CommitAsync();
    }

    [TestMethod]
    public async Task QuotaEngineCheckedOverflowInCommittedUsageOrActiveHoldsNeverAdmits()
    {
        ProjectsSeededUser committedUser = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(committedUser.OrganizationId, limit: long.MaxValue);
        await fixture.SeedQuotaUsageAsync(committedUser.OrganizationId, units: long.MaxValue);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(committedUser))
        {
            QuotaReservationResult result = await CreateQuota(scope)
                .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
            result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
            result.FailureCode.ShouldBe("quota_usage_invalid");
            result.Reservation.ShouldBeNull();
            await scope.CommitAsync();
        }

        ProjectsSeededUser heldUser = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(heldUser.OrganizationId, limit: long.MaxValue);
        await fixture.SeedExpiredActiveReservationAsync(heldUser, units: long.MaxValue);
        await using MutationScope heldScope = await fixture.BeginMutationScopeAsync(heldUser);
        QuotaReservationResult heldResult = await CreateQuota(heldScope)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        heldResult.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
        heldResult.FailureCode.ShouldBe("quota_usage_invalid");
        heldResult.Reservation.ShouldBeNull();
        await heldScope.CommitAsync();
    }

    [TestMethod]
    public async Task QuotaEngineMissingSharedTransactionIsRejectedBeforeReserving()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        await using MutationScope unenlisted = await fixture.BeginUnenlistedMutationScopeAsync(user);

        await Should.ThrowAsync<InvalidOperationException>(() => CreateQuota(unenlisted)
            .ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid()));
        await unenlisted.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaEngineStaleMembershipMismatchOrSuspensionCannotDiscloseAnExistingHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10);
        Guid operationId = Guid.NewGuid();
        await using (MutationScope original = await fixture.BeginMutationScopeAsync(user))
        {
            QuotaReservationResult result = await CreateQuota(original)
                .ReserveAsync(DocumentsQuotaResource, 2, operationId);
            result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await original.CommitAsync();
        }

        await using (MutationScope mismatch = await fixture.BeginMutationScopeAsync(user, Guid.NewGuid()))
        {
            QuotaReservationResult result = await CreateQuota(mismatch)
                .ReserveAsync(DocumentsQuotaResource, 2, operationId);
            result.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
            result.Reservation.ShouldBeNull();
            await mismatch.CommitAsync();
        }

        await using MutationScope stale = await fixture.BeginMutationScopeAsync(user);
        await fixture.SuspendMembershipAsync(user.MembershipId);
        QuotaReservationResult revoked = await CreateQuota(stale)
            .ReserveAsync(DocumentsQuotaResource, 2, operationId);
        revoked.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        revoked.Reservation.ShouldBeNull();
        await stale.CommitAsync();
    }

    private static OrganizationQuotaService CreateQuota(MutationScope scope) =>
        ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services);

    private sealed partial class ExecutionFixture
    {
        public Task SeedQuotaProjectionAsync(
            Guid organizationId,
            long? limit,
            OrganizationEntitlementMode mode = OrganizationEntitlementMode.Finite,
            bool inventoryComplete = true,
            ModuleQuotaUnit unit = ModuleQuotaUnit.Bytes) => SeedQuotaProjectionCoreAsync(
                organizationId, limit, mode, inventoryComplete, unit);

        private async Task SeedQuotaProjectionCoreAsync(
            Guid organizationId,
            long? limit,
            OrganizationEntitlementMode mode,
            bool inventoryComplete,
            ModuleQuotaUnit unit)
        {
            await ExecuteOwnerAsync("""
            INSERT INTO app.organization_entitlements
                ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version",
                 "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
            VALUES (@organization, 'documents.storage-bytes', 'documents', @unit, @mode, @limit, @version,
                    'host-configuration', 1, @hash, CURRENT_TIMESTAMP);
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            VALUES (@organization, 1, @hash, 'host-configuration', 1, @publisher, 1, CURRENT_TIMESTAMP)
            """, ("organization", organizationId), ("unit", (int)unit), ("mode", (int)mode),
                ("limit", (object?)limit ?? DBNull.Value), ("version", Guid.NewGuid()), ("hash", new string('a', 64)),
                ("publisher", HostEntitlementPublisher.AuditActorId));
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_quota_inventory
                    ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
                VALUES (@organization, 'documents.storage-bytes', @source, @sourceVersion, NULL, @state, CURRENT_TIMESTAMP)
                """, ("organization", organizationId),
                ("source", inventoryComplete ? "documents.inventory" : (object)DBNull.Value),
                ("sourceVersion", inventoryComplete ? (object)1 : DBNull.Value),
                ("state", inventoryComplete ? 1 : 0));
        }

        public Task SetQuotaProjectionAsync(Guid organizationId, long? limit) => ExecuteOwnerAsync("""
            WITH changed AS (UPDATE app.organization_entitlements
            SET "Mode" = 1, "Limit" = @limit, "Version" = @version, "SourceRevision" = "SourceRevision" + 1,
                "SourceHash" = @hash, "EffectiveAtUtc" = CURRENT_TIMESTAMP
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
            RETURNING "OrganizationId", "SourceRevision", "SourceHash")
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            SELECT "OrganizationId", "SourceRevision", "SourceHash", 'host-configuration', 1, @publisher, 1, CURRENT_TIMESTAMP FROM changed
            """, ("organization", organizationId), ("limit", (object?)limit ?? DBNull.Value),
            ("version", Guid.NewGuid()), ("hash", new string('b', 64)), ("publisher", HostEntitlementPublisher.AuditActorId));

        public async Task SeedExpiredActiveReservationAsync(ProjectsSeededUser user, long units)
        {
            Guid version = await ReadQuotaVersionAsync(user.OrganizationId);
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                     "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                VALUES (@organization, @id, 'documents.storage-bytes', @actor, @units, @operation, @version,
                        1, 0, CURRENT_TIMESTAMP - INTERVAL '2 hours', CURRENT_TIMESTAMP - INTERVAL '1 hour', NULL)
                """, ("organization", user.OrganizationId), ("id", Guid.NewGuid()), ("actor", user.ActorId),
                ("units", units), ("operation", Guid.NewGuid()), ("version", version));
        }

        public async Task SeedQuotaUsageAsync(Guid organizationId, long units)
        {
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_usage
                    ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
                VALUES (@organization, 'documents.storage-bytes', @effect, @source, NULL, NULL, @actor, @units, CURRENT_TIMESTAMP)
                """, ("organization", organizationId), ("effect", Guid.NewGuid()), ("source", Guid.NewGuid()),
                ("actor", Guid.NewGuid()), ("units", units));
        }

        public Task SuspendMembershipAsync(Guid membershipId) => ExecuteOwnerAsync("""
            UPDATE platform.memberships SET "Status" = 2 WHERE "Id" = @membership
            """, ("membership", membershipId));

        public Task<long> CountQuotaRowsAsync(string table, Guid organizationId) => CountOwnerAsync(
            $"SELECT count(*) FROM app.{table} WHERE \"OrganizationId\" = @organization",
            ("organization", organizationId));

        public Task<long> CountQuotaReservationsForOperationAsync(Guid organizationId, Guid operationId) => CountOwnerAsync("""
            SELECT count(*) FROM app.organization_quota_reservations
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes' AND "OperationId" = @operation
            """, ("organization", organizationId), ("operation", operationId));

        public Task<long> SumQuotaUsageAsync(Guid organizationId) => CountOwnerAsync("""
            SELECT COALESCE(sum("Units"), 0) FROM app.organization_usage
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
            """, ("organization", organizationId));

        private async Task<Guid> ReadQuotaVersionAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "Version" FROM app.organization_entitlements
                WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }
    }
}
