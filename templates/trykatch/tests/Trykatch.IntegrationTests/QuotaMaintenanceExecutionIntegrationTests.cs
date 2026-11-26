using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Trykatch.Application;
using Trykatch.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;
using Shouldly;
using Npgsql;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task ExpiredTransactionOnlyHoldIsReleasedExactlyOnceByQueuedMaintenance()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        (Guid reservationId, Guid workId) = await fixture.MaintenanceExecutionSeedExpiredTransactionHoldAsync(user);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        QuotaMaintenanceProcessor processor = environment.Services.GetRequiredService<QuotaMaintenanceProcessor>();

        (await processor.ProcessAsync(workId, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await processor.ProcessAsync(workId, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);

        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, reservationId)).ShouldBe(2);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransactionOnlyWorkCannotReleaseAnUploadReservationWithoutFencingIt(bool removeUploadRecord)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
        }

        Guid reservationId = await fixture.MaintenanceExecutionReadUploadReservationAsync(user.OrganizationId, receipt.OperationId);
        Guid wrongKindWorkId = await fixture.MaintenanceExecutionInsertTransactionOnlyWorkAsync(user.OrganizationId, reservationId);
        if (removeUploadRecord)
            await fixture.MaintenanceExecutionRemoveUploadIntentAsync(user.OrganizationId, receipt.OperationId);
        QuotaMaintenanceProcessor processor = environment.Services.GetRequiredService<QuotaMaintenanceProcessor>();

        QuotaMaintenanceResult result = await processor.ProcessAsync(wrongKindWorkId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Unavailable);
        if (!removeUploadRecord)
            (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
        (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, reservationId)).ShouldBe(0);
        (await environment.Services.GetRequiredService<IConditionalObjectStorage>().StatAsync(receipt.ObjectKey)).State
            .ShouldBe(ConditionalObjectState.Missing);
    }

    [TestMethod]
    public async Task ExpiredUploadBeforePutIsFencedAndItsHoldReleased()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(clock));
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);

        QuotaMaintenanceResult result = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(3);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        ConditionalObjectStat sentinel = await environment.Services.GetRequiredService<IConditionalObjectStorage>()
            .StatAsync(receipt.ObjectKey);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task ExpiredUploadAfterPutIsFencedAndLateWriterCannotRestoreItsBytes()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(clock));
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(scope.Services, receipt);
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);

        QuotaMaintenanceResult result = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Cancellation);
        using MemoryStream lateBytes = new("admitted module upload"u8.ToArray());
        (await storage.TryCreateAsync(receipt.ObjectKey, lateBytes, 22, "text/plain", receipt.Identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Cancellation);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task DueMaintenanceCannotFenceACommittedUpload()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = scope.Services.GetRequiredService<IModuleUploadAdmission>();
            receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(scope.Services, receipt);
            (await admission.FinalizeAsync(receipt, _ => CreateProjectAndSaveAuditAsync(scope, "Committed maintenance target")))
                .Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Committed);
            await scope.CommitAsync();
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        await fixture.MaintenanceExecutionMakeWorkDueAsync(workId);

        QuotaMaintenanceResult result = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(1);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(1);
        (await environment.Services.GetRequiredService<IConditionalObjectStorage>().StatAsync(receipt.ObjectKey)).State
            .ShouldBe(ConditionalObjectState.Provisional);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IneligibleOrganizationPausesMaintenanceAndResumesWhenEligible(bool unsupportedPlacement)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(clock));
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        if (unsupportedPlacement) await fixture.SetQuotaMaintenanceProviderAsync(user.OrganizationId, "unsupported");
        else await fixture.SetOrganizationActiveAsync(user.OrganizationId, false);

        QuotaMaintenanceResult inactive = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        inactive.State.ShouldBe(QuotaMaintenanceExecutionState.Paused);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(0);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(0);
        if (unsupportedPlacement) await fixture.SetQuotaMaintenanceProviderAsync(user.OrganizationId, "postgres");
        else await fixture.SetOrganizationActiveAsync(user.OrganizationId, true);
        await fixture.MaintenanceExecutionMakeWorkDueAsync(workId);

        QuotaMaintenanceResult resumed = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        resumed.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(3);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task InventoryMaintenanceWritesCheckpointAndUsageBeforeCompletingWork()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64, inventoryComplete: false);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 11);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            ModuleUploadReserveResult pending = await scope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(CreateUploadRequest());
            pending.Disposition.ShouldBe(ModuleUploadReserveDisposition.Unavailable);
            pending.FailureCode.ShouldBe("quota_inventory_initializing");
        }
        Guid workId = await fixture.MaintenanceExecutionFindInventoryWorkAsync(user.OrganizationId);

        QuotaMaintenanceResult result = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldContain(entry => entry.Units == 11);
        (await fixture.MaintenanceExecutionReadInventoryCheckpointAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task StorageFailureRetainsUploadFencingAndHoldForRetryWithoutAttemptCutoff()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        FailStatObjectStorage? failingStorage = null;
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IConditionalObjectStorage>(provider => failingStorage = new FailStatObjectStorage(
                provider.GetRequiredService<LocalObjectStorage>()));
        });
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        await fixture.MaintenanceExecutionSetAttemptsAsync(workId, 5);

        QuotaMaintenanceResult result = await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None);

        result.State.ShouldBe(QuotaMaintenanceExecutionState.Unavailable);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(0);
        (await fixture.MaintenanceExecutionReadAttemptsAsync(workId)).ShouldBeGreaterThan(5);
        failingStorage.ShouldNotBeNull();
        failingStorage.Fail = false;
        await fixture.MaintenanceExecutionMakeWorkDueAsync(workId);
        QuotaMaintenanceProcessor restarted = ActivatorUtilities.CreateInstance<QuotaMaintenanceProcessor>(environment.Services);
        (await restarted.ProcessAsync(workId, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        (await failingStorage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Cancellation);
    }

    [TestMethod]
    public async Task CompetingMaintenanceClaimsAreLeaseExclusiveAndRecoverable()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(clock));
        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        QuotaMaintenanceStore store = environment.Services.GetRequiredService<QuotaMaintenanceStore>();

        QuotaMaintenanceClaim? first = await store.ClaimAsync(workId, CancellationToken.None);
        QuotaMaintenanceClaim? competing = await store.ClaimAsync(workId, CancellationToken.None);

        first.ShouldNotBeNull();
        competing.ShouldBeNull();
        first!.LeaseId.ShouldNotBe(Guid.Empty);
        (await store.FinishAsync(first, new(QuotaMaintenanceExecutionState.Paused), CancellationToken.None)).ShouldBeTrue();
        await fixture.MaintenanceExecutionMakeWorkDueAsync(workId);
        (await environment.Services.GetRequiredService<QuotaMaintenanceProcessor>()
            .ProcessAsync(workId, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task AbsentDocumentsModuleStillFencesPreviouslyAdmittedUploadWithoutOriginalMembership()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment admitted = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(new UploadClock(DateTimeOffset.UtcNow.AddMinutes(-20))));
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(admitted.Services, user))
            receipt = (await parent.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
        receipt.ShouldNotBeNull();
        (await fixture.RevokeMembershipAsync(user.OrganizationId, user.ActorId)).ShouldBe(1);
        IConfiguration configuration = admitted.Services.GetRequiredService<IConfiguration>();
        ServiceCollection registrations = new();
        registrations.AddSingleton(configuration);
        registrations.AddApplication();
        registrations.AddModules(configuration, [new ProjectsModule()]);
        registrations.AddInfrastructure(configuration);
        await using ServiceProvider recovery = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        recovery.GetRequiredService<ModuleCatalog>().Contains("documents").ShouldBeFalse();
        IConditionalObjectStorage storage = recovery.GetRequiredService<IConditionalObjectStorage>();
        storage.NamespaceFingerprint.ShouldBe(admitted.Services.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint);
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);

        (await recovery.GetRequiredService<QuotaMaintenanceProcessor>().ProcessAsync(workId, CancellationToken.None))
            .State.ShouldBe(QuotaMaintenanceExecutionState.Completed);

        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Cancellation);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task RegisteredMaintenanceWorkerProcessesDurableWorkAndStopsCleanly()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        (Guid hold, Guid work) = await fixture.MaintenanceExecutionSeedExpiredTransactionHoldAsync(user);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(registrations =>
        {
            // Exercise the registered quota worker without activating unrelated scheduler hosted services.
            foreach (ServiceDescriptor service in registrations.Where(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType?.Name != "QuotaMaintenanceWorker").ToArray())
                registrations.Remove(service);
        });
        IHostedService worker = environment.Services.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "QuotaMaintenanceWorker");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            while (await fixture.ReadQuotaReservationStateAsync(user.OrganizationId, hold) != 2
                || (await fixture.ReadQuotaMaintenanceWorkAsync(work)).State != 3)
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            environment.Services.GetRequiredService<QuotaMaintenanceWorkerState>().Read().Unavailable.ShouldBeFalse();
        }
        finally { await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task MissingAcknowledgementAfterFencingCommitIsRecoveredWithoutRepeatingStorageMutation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(new UploadClock(DateTimeOffset.UtcNow.AddMinutes(-20))));
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await parent.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest())).Receipt!;
            await PutAdmittedUploadAsync(parent.Services, receipt);
        }
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        QuotaMaintenanceStore originalStore = environment.Services.GetRequiredService<QuotaMaintenanceStore>();
        QuotaMaintenanceClaim claim = (await originalStore.ClaimAsync(workId, CancellationToken.None))!;
        claim.ShouldNotBeNull();
        await using (AsyncServiceScope execution = environment.Services.CreateAsyncScope())
            (await execution.ServiceProvider.GetRequiredService<QuotaMaintenanceExecutor>()
                .ExecuteAsync(claim.Work, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        ObjectStorageRevision? confirmed = (await storage.StatAsync(receipt.ObjectKey)).Revision;
        confirmed.ShouldNotBeNull();
        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(1);
        await fixture.ExpireQuotaMaintenanceLeaseAsync(workId);

        QuotaMaintenanceProcessor restarted = ActivatorUtilities.CreateInstance<QuotaMaintenanceProcessor>(environment.Services);
        (await restarted.ProcessAsync(workId, CancellationToken.None)).State.ShouldBe(QuotaMaintenanceExecutionState.Completed);

        (await storage.StatAsync(receipt.ObjectKey)).Revision.ShouldBe(confirmed);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        (await fixture.ReadQuotaMaintenanceWorkAsync(workId)).State.ShouldBe(3);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    private sealed partial class ExecutionFixture
    {
        public async Task<(Guid ReservationId, Guid WorkId)> MaintenanceExecutionSeedExpiredTransactionHoldAsync(
            ProjectsSeededUser user)
        {
            Guid reservationId = Guid.CreateVersion7();
            Guid workId = Guid.CreateVersion7();
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                     "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                SELECT @organization, @reservation, 'documents.storage-bytes', @actor, 7, @operation, "Version",
                    1, 0, CURRENT_TIMESTAMP - INTERVAL '2 hours', CURRENT_TIMESTAMP - INTERVAL '1 hour', NULL
                FROM app.organization_entitlements
                WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes';
                INSERT INTO infrastructure.quota_maintenance_work
                    ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State",
                     "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
                VALUES (@organization, @work, 1, 'documents.storage-bytes', @reservation, NULL, 0,
                    CURRENT_TIMESTAMP - INTERVAL '1 hour', CURRENT_TIMESTAMP, 0, NULL, NULL, NULL)
                """, ("organization", user.OrganizationId), ("reservation", reservationId), ("operation", Guid.CreateVersion7()),
                ("actor", user.ActorId), ("work", workId));
            return (reservationId, workId);
        }

        public async Task<Guid> MaintenanceExecutionInsertTransactionOnlyWorkAsync(
            Guid organizationId, Guid reservationId)
        {
            Guid workId = Guid.CreateVersion7();
            await ExecuteOwnerAsync("""
                INSERT INTO infrastructure.quota_maintenance_work
                    ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State",
                     "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
                VALUES (@organization, @work, 1, 'documents.storage-bytes', @reservation, NULL, 0,
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 0, NULL, NULL, NULL)
                """, ("organization", organizationId), ("work", workId), ("reservation", reservationId));
            return workId;
        }

        public Task<Guid> MaintenanceExecutionReadUploadReservationAsync(Guid organizationId, Guid operationId) =>
            MaintenanceExecutionScalarGuidAsync("""
                SELECT "ReservationId" FROM app.module_upload_operations
                WHERE "OrganizationId"=@organization AND "OperationId"=@operation
                """, ("organization", organizationId), ("operation", operationId));

        public Task MaintenanceExecutionRemoveUploadIntentAsync(Guid organizationId, Guid operationId) => ExecuteOwnerAsync("""
            DELETE FROM infrastructure.quota_maintenance_work
            WHERE "OrganizationId"=@organization AND "UploadOperationId"=@operation;
            DELETE FROM app.module_upload_operations
            WHERE "OrganizationId"=@organization AND "OperationId"=@operation
            """, ("organization", organizationId), ("operation", operationId));

        public async Task<Guid> MaintenanceExecutionFindUploadWorkAsync(Guid organizationId, Guid operationId) =>
            await MaintenanceExecutionScalarGuidAsync("""
                SELECT "Id" FROM infrastructure.quota_maintenance_work
                WHERE "OrganizationId"=@organization AND "UploadOperationId"=@operation AND "Kind"=2
                """, ("organization", organizationId), ("operation", operationId));

        public async Task<Guid> MaintenanceExecutionFindInventoryWorkAsync(Guid organizationId) =>
            await MaintenanceExecutionScalarGuidAsync("""
                SELECT "Id" FROM infrastructure.quota_maintenance_work
                WHERE "OrganizationId"=@organization AND "Kind"=3 AND "UploadOperationId" IS NULL
                ORDER BY "CreatedAtUtc" DESC LIMIT 1
                """, ("organization", organizationId));

        public Task MaintenanceExecutionMakeWorkDueAsync(Guid workId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.quota_maintenance_work SET \"DueAtUtc\"=CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE \"Id\"=@work",
            ("work", workId));

        public Task MaintenanceExecutionSetAttemptsAsync(Guid workId, int attempts) => ExecuteOwnerAsync(
            "UPDATE infrastructure.quota_maintenance_work SET \"Attempts\"=@attempts WHERE \"Id\"=@work",
            ("work", workId), ("attempts", attempts));

        public Task<long> MaintenanceExecutionReadAttemptsAsync(Guid workId) => CountOwnerAsync(
            "SELECT \"Attempts\"::bigint FROM infrastructure.quota_maintenance_work WHERE \"Id\"=@work", ("work", workId));

        public Task<long> MaintenanceExecutionReadInventoryCheckpointAsync(Guid organizationId) => CountOwnerAsync(
            "SELECT \"State\"::bigint FROM app.organization_quota_inventory WHERE \"OrganizationId\"=@organization AND \"ResourceKey\"='documents.storage-bytes'",
            ("organization", organizationId));

        private async Task<Guid> MaintenanceExecutionScalarGuidAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }
    }

    private sealed class FailStatObjectStorage(IConditionalObjectStorage inner) : IConditionalObjectStorage
    {
        public bool Fail { get; set; } = true;
        public string NamespaceFingerprint => inner.NamespaceFingerprint;
        public Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default) => Fail
            ? throw new IOException("Injected storage inspection failure.") : inner.StatAsync(key, cancellationToken);
        public Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length, string contentType,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default) =>
            inner.TryCreateAsync(key, content, length, contentType, identity, cancellationToken);
        public Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key, ObjectStorageRevision? expectedVersion,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default) =>
            inner.TryReplaceWithCancellationAsync(key, expectedVersion, identity, cancellationToken);
    }
}
