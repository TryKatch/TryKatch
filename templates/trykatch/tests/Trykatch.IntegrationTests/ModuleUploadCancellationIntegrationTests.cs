using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task CancelAfterParentRollbackAndMembershipRevocationFencesObjectBeforeReleasingHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        ModuleUploadRequest request = CreateUploadRequest();
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(services =>
            services.AddSingleton<IConditionalObjectStorage>(provider => new ObserveCancellationStorage(
                provider.GetRequiredService<LocalObjectStorage>(), async () =>
                {
                    // A separate owner connection must observe the committed fence before object mutation.
                    (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, request.OperationId)).ShouldBe(2);
                    (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
                })));
        IModuleUploadAdmission admission;
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(parent.Services, receipt);
        }
        await fixture.SuspendMembershipAsync(user.MembershipId);

        ModuleUploadCancelResult cancelled = await admission.CancelAsync(receipt);

        cancelled.Disposition.ShouldBe(ModuleUploadCancelDisposition.Cancelled);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(3);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        ConditionalObjectStat sentinel = await storage.StatAsync(receipt.ObjectKey);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        sentinel.Identity.ShouldBe(receipt.Identity);
        (await admission.CancelAsync(receipt)).Disposition.ShouldBe(ModuleUploadCancelDisposition.AlreadyCancelled);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task CancellingMissingUploadCreatesSentinelThatRejectsALateWriter()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        IModuleUploadAdmission admission;
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            receipt = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        (await admission.CancelAsync(receipt)).Disposition.ShouldBe(ModuleUploadCancelDisposition.Cancelled);
        using MemoryStream lateBytes = new("admitted module upload"u8.ToArray());
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        ConditionalObjectWriteResult late = await storage.TryCreateAsync(receipt.ObjectKey, lateBytes, 22, "text/plain", receipt.Identity);
        late.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Cancellation);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task CancellationCannotTouchAnUploadWhileFinalizationOwnsLockOrAfterItCommits()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
        ModuleUploadReceipt receipt = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
        receipt.ShouldNotBeNull();
        await PutAdmittedUploadAsync(parent.Services, receipt);
        (await admission.FinalizeAsync(receipt, _ => CreateProjectAndSaveAuditAsync(parent, "Protected upload")))
            .Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Committed);

        ModuleUploadCancelResult busy = await admission.CancelAsync(receipt).WaitAsync(TimeSpan.FromSeconds(2));
        busy.Disposition.ShouldBe(ModuleUploadCancelDisposition.Unavailable);
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Provisional);
        await parent.CommitAsync();
        (await admission.CancelAsync(receipt)).Disposition.ShouldBe(ModuleUploadCancelDisposition.Conflict);
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Provisional);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(1);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task ForeignStoredIdentityRetainsFencingHoldAndCannotBeOverwrittenByCancellation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        IModuleUploadAdmission admission;
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            receipt = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
        }
        IConditionalObjectStorage storage = environment.Services.GetRequiredService<IConditionalObjectStorage>();
        ObjectWriteIdentity foreign = receipt.Identity with { OperationId = Guid.NewGuid() };
        using MemoryStream bytes = new("admitted module upload"u8.ToArray());
        (await storage.TryCreateAsync(receipt.ObjectKey, bytes, 22, "text/plain", foreign))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);

        (await admission.CancelAsync(receipt)).Disposition.ShouldBe(ModuleUploadCancelDisposition.Unavailable);

        (await storage.StatAsync(receipt.ObjectKey)).Identity.ShouldBe(foreign);
        (await storage.StatAsync(receipt.ObjectKey)).State.ShouldBe(ConditionalObjectState.Provisional);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(2);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, receipt.OperationId)).ShouldBe(0);
    }

    private sealed partial class ExecutionFixture
    {
        public Task<long> ReadUploadLifecycleAsync(Guid organizationId, Guid operationId) => CountOwnerAsync("""
            SELECT "State" FROM app.module_upload_operations WHERE "OrganizationId"=@organization AND "OperationId"=@operation
            """, ("organization", organizationId), ("operation", operationId));

        public Task<long> ReadUploadHoldStateAsync(Guid organizationId, Guid operationId) => CountOwnerAsync("""
            SELECT "State" FROM app.organization_quota_reservations WHERE "OrganizationId"=@organization AND "OperationId"=@operation
            """, ("organization", organizationId), ("operation", operationId));
    }

    private sealed class ObserveCancellationStorage(IConditionalObjectStorage inner, Func<Task> beforeCancellation) : IConditionalObjectStorage
    {
        public string NamespaceFingerprint => inner.NamespaceFingerprint;
        public Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default) =>
            inner.StatAsync(key, cancellationToken);
        public Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length, string contentType,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default) =>
            inner.TryCreateAsync(key, content, length, contentType, identity, cancellationToken);
        public async Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key, ObjectStorageRevision? expectedVersion,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
        {
            await beforeCancellation();
            return await inner.TryReplaceWithCancellationAsync(key, expectedVersion, identity, cancellationToken);
        }
    }
}
