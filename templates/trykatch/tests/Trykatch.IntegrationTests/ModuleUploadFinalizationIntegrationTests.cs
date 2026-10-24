using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task UploadAccountingFailureAfterMetadataFlushPoisonsTheOwningTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        ModuleUploadRequest request = CreateUploadRequest();
        await fixture.SeedHistoricalInventoryEffectAsync(user.OrganizationId, request.OperationId, Guid.NewGuid(), 1);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            ModuleUploadReceipt receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(parent.Services, receipt);
            ModuleUploadFinalization<Guid> result = await admission.FinalizeAsync(receipt,
                _ => CreateProjectAndSaveAuditAsync(parent, "Accounting must abort metadata"));
            result.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Unavailable);
            await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => parent.CommitAsync());
        }
        (await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId)).ShouldBeEmpty();
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).Single().Units.ShouldBe(1);
        (await fixture.ReadUploadLifecycleAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TwoPreAdmittedUploadsCommitTogetherOrBothRollBackOnSecondCallbackFailure(bool failSecond)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            ModuleUploadReceipt first = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
            ModuleUploadReceipt second = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
            first.ShouldNotBeNull();
            second.ShouldNotBeNull();
            await PutAdmittedUploadAsync(parent.Services, first);
            await PutAdmittedUploadAsync(parent.Services, second);
            (await admission.FinalizeAsync(first, _ => CreateProjectAndSaveAuditAsync(parent, "First upload")))
                .Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Committed);
            if (failSecond)
            {
                await Should.ThrowAsync<IOException>(() => admission.FinalizeAsync<Guid>(second, async _ =>
                {
                    await CreateProjectAndSaveAuditAsync(parent, "Second upload fails after save");
                    throw new IOException("Second callback failure.");
                }));
                await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => parent.CommitAsync());
            }
            else
            {
                (await admission.FinalizeAsync(second, _ => CreateProjectAndSaveAuditAsync(parent, "Second upload")))
                    .Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Committed);
                await parent.CommitAsync();
            }
        }
        ProjectProducerSnapshot effects = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        effects.ProjectCount.ShouldBe(failSecond ? 0 : 2);
        effects.AuditCount.ShouldBe(failSecond ? 0 : 2);
        effects.OutboxRows.Count.ShouldBe(failSecond ? 0 : 4);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(failSecond ? 0 : 2);
    }

    [TestMethod]
    public async Task OwnedUploadFinalizesAtomicallyAndCommittedRetrySkipsMetadataCallback()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        Guid subject;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            ModuleUploadReceipt receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            subject = receipt.SubjectId;
            await PutAdmittedUploadAsync(parent.Services, receipt);
            ModuleUploadFinalization<Guid> finalized = await admission.FinalizeAsync(receipt,
                _ => CreateProjectAndSaveAuditAsync(parent, "Atomic uploaded metadata"));
            finalized.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Committed);
            finalized.SubjectId.ShouldBe(subject);
            finalized.Value.ShouldNotBe(Guid.Empty);
            await parent.CommitAsync();
        }
        await using (MutationScope retry = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = retry.Services.GetRequiredService<IModuleUploadAdmission>();
            ModuleUploadReserveResult reserved = await admission.ReserveAsync(request);
            reserved.Disposition.ShouldBe(ModuleUploadReserveDisposition.AlreadyCommitted);
            reserved.Receipt.ShouldNotBeNull();
            ModuleUploadFinalization<Guid> replay = await admission.FinalizeAsync<Guid>(reserved.Receipt!,
                _ => throw new InvalidOperationException("A committed retry cannot repeat metadata work."));
            replay.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.AlreadyCommitted);
            replay.SubjectId.ShouldBe(subject);
            await retry.CommitAsync();
        }
        ProjectProducerSnapshot effects = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        effects.ProjectCount.ShouldBe(1);
        effects.AuditCount.ShouldBe(1);
        effects.OutboxRows.Count.ShouldBe(2);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(1);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).Single().ShouldBe((request.OperationId, subject, 22L));
    }

    [TestMethod]
    public async Task UploadCallbackFailureAfterFlushForcesParentRollbackAndRetainsDurableAdmission()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            ModuleUploadReceipt receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(parent.Services, receipt);
            await Should.ThrowAsync<IOException>(() => admission.FinalizeAsync<Guid>(receipt, async _ =>
            {
                await CreateProjectAndSaveAuditAsync(parent, "Must roll back flushed metadata");
                throw new IOException("Callback failed after SaveChanges.");
            }));
            await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => parent.CommitAsync());
        }
        (await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId)).ShouldBeEmpty();
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, request.OperationId)).ShouldBe(1);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task MissingUploadObjectCannotInvokeMetadataAndRevokedMembershipCannotFinalize()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage", "projects.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
        ModuleUploadReceipt receipt = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
        receipt.ShouldNotBeNull();
        ModuleUploadFinalization<Guid> missing = await admission.FinalizeAsync<Guid>(receipt,
            _ => throw new InvalidOperationException("A missing object cannot invoke metadata work."));
        missing.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Unavailable);
        missing.FailureCode.ShouldBe("upload_object_unverified");
        await PutAdmittedUploadAsync(parent.Services, receipt);
        await fixture.SuspendMembershipAsync(user.MembershipId);
        ModuleUploadFinalization<Guid> revoked = await admission.FinalizeAsync<Guid>(receipt,
            _ => throw new InvalidOperationException("A revoked caller cannot invoke metadata work."));
        revoked.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Unavailable);
        revoked.FailureCode.ShouldBe("upload_authority_denied");
        await parent.CommitAsync();
        (await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId)).ShouldBeEmpty();
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task CopiedOrCrossScopeReceiptDoesNotConferFinalizationAuthority()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadReceipt original;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            original = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
            original.ShouldNotBeNull();
            ModuleUploadFinalization<Guid> forged = await admission.FinalizeAsync<Guid>(new CopiedUploadReceipt(original),
                _ => throw new InvalidOperationException("A copied receipt cannot invoke metadata work."));
            forged.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Conflict);
        }
        await using MutationScope other = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ModuleUploadFinalization<Guid> crossScope = await other.Services.GetRequiredService<IModuleUploadAdmission>()
            .FinalizeAsync<Guid>(original, _ => throw new InvalidOperationException("A foreign issuer cannot finalize."));
        crossScope.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Conflict);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    private static async Task PutAdmittedUploadAsync(IServiceProvider services, ModuleUploadReceipt receipt)
    {
        using MemoryStream stream = new("admitted module upload"u8.ToArray());
        ConditionalObjectWriteResult result = await services.GetRequiredService<IConditionalObjectStorage>()
            .TryCreateAsync(receipt.ObjectKey, stream, 22, "text/plain", receipt.Identity);
        result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
    }

    private sealed class CopiedUploadReceipt(ModuleUploadReceipt original) : ModuleUploadReceipt
    {
        public override ModuleUploadPolicyRef Policy => original.Policy;
        public override Guid OperationId => original.OperationId;
        public override Guid SubjectId => original.SubjectId;
        public override string ObjectKey => original.ObjectKey;
        public override ObjectWriteIdentity Identity => original.Identity;
        public override DateTimeOffset ExpiresAtUtc => original.ExpiresAtUtc;
    }
}
