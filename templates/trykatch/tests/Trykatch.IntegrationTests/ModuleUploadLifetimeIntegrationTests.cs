using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task FiniteUploadCapacityCountsDurableHoldsAndOnlyAConfirmedFenceRestoresItsLastSlot()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 22);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
        ModuleUploadReceipt first = (await admission.ReserveAsync(CreateUploadRequest())).Receipt!;
        first.ShouldNotBeNull();
        ModuleUploadReserveResult denied = await admission.ReserveAsync(CreateUploadRequest());
        denied.Disposition.ShouldBe(ModuleUploadReserveDisposition.Denied);
        denied.FailureCode.ShouldBe("quota_exhausted");
        denied.Receipt.ShouldBeNull();
        (await admission.CancelAsync(first)).Disposition.ShouldBe(ModuleUploadCancelDisposition.Cancelled);
        ModuleUploadReserveResult replacement = await admission.ReserveAsync(CreateUploadRequest());
        replacement.Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
        replacement.Receipt!.SubjectId.ShouldNotBe(first.SubjectId);
        replacement.Receipt.ObjectKey.ShouldNotBe(first.ObjectKey);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(2);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, first.OperationId)).ShouldBe(2);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, replacement.Receipt.OperationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task FileLargerThanTheFiniteUploadAllowanceCannotCreateAHoldOrOperation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 21);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ModuleUploadReserveResult denied = await parent.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(CreateUploadRequest());
        denied.Disposition.ShouldBe(ModuleUploadReserveDisposition.Denied);
        denied.FailureCode.ShouldBe("quota_exhausted");
        denied.Receipt.ShouldBeNull();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadReceiptCannotFinalizeInANewTransactionLifetimeWithinTheSameScopedService()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IModuleUploadAdmission admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
        ModuleUploadRequest request = CreateUploadRequest();
        ModuleUploadReceipt original = (await admission.ReserveAsync(request)).Receipt!;
        original.ShouldNotBeNull();
        await parent.Services.GetRequiredService<IOrganizationEntitlementReader>().GetAsync(DocumentsQuotaResource);
        OrganizationControlPlaneDbContext control = parent.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = parent.Services.GetRequiredService<ApplicationDbContext>();
        IDbContextTransaction old = control.Database.CurrentTransaction!;
        IDbContextTransaction oldApplication = application.Database.CurrentTransaction!;
        Guid oldId = old.TransactionId;
        await old.RollbackAsync();
        await oldApplication.DisposeAsync();
        await old.DisposeAsync();

        await using IOrganizationDataScope next = await parent.Services.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(user.OrganizationId, user.ActorId, CancellationToken.None);
        _ = await OrganizationTransactionEnlistment.EnlistAsync(control, application, user.OrganizationId, user.ActorId, CancellationToken.None);
        control.Database.CurrentTransaction!.TransactionId.ShouldNotBe(oldId);
        ModuleUploadReserveResult renewed = await admission.ReserveAsync(request);
        renewed.Disposition.ShouldBe(ModuleUploadReserveDisposition.AlreadyReserved);
        renewed.Receipt!.SubjectId.ShouldBe(original.SubjectId);
        ModuleUploadFinalization<Guid> rejected = await admission.FinalizeAsync<Guid>(original,
            _ => throw new InvalidOperationException("An earlier transaction receipt cannot invoke metadata work."));
        rejected.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Conflict);
        await next.CommitAsync(CancellationToken.None);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task ExpiredAdmissionCannotBeRenewedOrFinalizedAndCanStillBeFenced()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UploadClock clock = new(DateTimeOffset.UtcNow);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(
            services => services.AddSingleton<TimeProvider>(clock));
        ModuleUploadRequest request = CreateUploadRequest();
        IModuleUploadAdmission admission;
        ModuleUploadReceipt receipt;
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            admission = parent.Services.GetRequiredService<IModuleUploadAdmission>();
            receipt = (await admission.ReserveAsync(request)).Receipt!;
            receipt.ShouldNotBeNull();
            clock.Current += TimeSpan.FromMinutes(16);
            ModuleUploadReserveResult expired = await admission.ReserveAsync(request);
            expired.Disposition.ShouldBe(ModuleUploadReserveDisposition.Unavailable);
            expired.FailureCode.ShouldBe("upload_expired");
            expired.Receipt.ShouldBeNull();
            ModuleUploadFinalization<Guid> finalization = await admission.FinalizeAsync<Guid>(receipt,
                _ => throw new InvalidOperationException("An expired upload cannot invoke metadata work."));
            finalization.Disposition.ShouldBe(ModuleUploadFinalizationDisposition.Unavailable);
            finalization.FailureCode.ShouldBe("upload_expired");
        }
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
        (await admission.CancelAsync(receipt)).Disposition.ShouldBe(ModuleUploadCancelDisposition.Cancelled);
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, request.OperationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task ChangedActualStorageNamespaceCannotRebindAnExistingUploadOperation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment original = fixture.CreateUploadAdmissionEnvironment();
        await using UploadAdmissionEnvironment changed = fixture.CreateUploadAdmissionEnvironment();
        original.Services.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint
            .ShouldNotBe(changed.Services.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint);
        ModuleUploadRequest request = CreateUploadRequest();
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(original.Services, user))
        {
            (await parent.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(request))
                .Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
        }
        await using MutationScope newDestination = await ExecutionFixture.BeginUploadMutationScopeAsync(changed.Services, user);
        ModuleUploadReserveResult conflict = await newDestination.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(request);
        conflict.Disposition.ShouldBe(ModuleUploadReserveDisposition.Conflict);
        conflict.Receipt.ShouldBeNull();
        (await fixture.ReadUploadHoldStateAsync(user.OrganizationId, request.OperationId)).ShouldBe(0);
    }

    private sealed class UploadClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Current { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
