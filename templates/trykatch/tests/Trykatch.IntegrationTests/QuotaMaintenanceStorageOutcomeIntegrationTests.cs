using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task SuccessfulReplacementWithUnavailableResponseIsIndependentlyConfirmed()
    {
        ExpiredUploadWork work = await CreateExpiredUploadWorkAsync(StorageFault.ReturnUnavailableAfterReplacement);
        await using (work.Environment)
        {
            QuotaMaintenanceResult result = await work.Environment.Services
                .GetRequiredService<QuotaMaintenanceProcessor>().ProcessAsync(work.WorkId, CancellationToken.None);

            result.State.ShouldBe(QuotaMaintenanceExecutionState.Completed);
            work.Storage.ReplacementCalls.ShouldBe(1);
            await AssertCancelledUploadAsync(work);
        }
    }

    [TestMethod]
    public async Task SuccessfulReplacementThenLostResponseRetainsHoldUntilRetryConfirmsSentinel()
    {
        ExpiredUploadWork work = await CreateExpiredUploadWorkAsync(StorageFault.ThrowAfterReplacement);
        await using (work.Environment)
        {
            QuotaMaintenanceProcessor processor = work.Environment.Services.GetRequiredService<QuotaMaintenanceProcessor>();
            (await processor.ProcessAsync(work.WorkId, CancellationToken.None)).State
                .ShouldBe(QuotaMaintenanceExecutionState.Unavailable);
            ObjectStorageRevision revision = await AssertFencingRetainedAsync(work);

            await fixture.MaintenanceExecutionMakeWorkDueAsync(work.WorkId);
            (await processor.ProcessAsync(work.WorkId, CancellationToken.None)).State
                .ShouldBe(QuotaMaintenanceExecutionState.Completed);

            work.Storage.ReplacementCalls.ShouldBe(1);
            (await work.Storage.StatAsync(work.Receipt.ObjectKey)).Revision.ShouldBe(revision);
            await AssertCancelledUploadAsync(work);
        }
    }

    [TestMethod]
    public async Task SuccessfulReplacementWithUnavailableConfirmationRetainsHoldUntilRetry()
    {
        ExpiredUploadWork work = await CreateExpiredUploadWorkAsync(StorageFault.FirstPostReplacementStatUnavailable);
        await using (work.Environment)
        {
            QuotaMaintenanceProcessor processor = work.Environment.Services.GetRequiredService<QuotaMaintenanceProcessor>();
            (await processor.ProcessAsync(work.WorkId, CancellationToken.None)).State
                .ShouldBe(QuotaMaintenanceExecutionState.Unavailable);
            ObjectStorageRevision revision = await AssertFencingRetainedAsync(work);

            await fixture.MaintenanceExecutionMakeWorkDueAsync(work.WorkId);
            (await processor.ProcessAsync(work.WorkId, CancellationToken.None)).State
                .ShouldBe(QuotaMaintenanceExecutionState.Completed);

            work.Storage.ReplacementCalls.ShouldBe(1);
            (await work.Storage.StatAsync(work.Receipt.ObjectKey)).Revision.ShouldBe(revision);
            await AssertCancelledUploadAsync(work);
        }
    }

    private static async Task<ExpiredUploadWork> CreateExpiredUploadWorkAsync(StorageFault fault)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        UncertainReplacementObjectStorage? storage = null;
        UploadClock clock = new(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IConditionalObjectStorage>(provider => storage = new UncertainReplacementObjectStorage(
                provider.GetRequiredService<LocalObjectStorage>(), fault));
        });

        ModuleUploadReceipt receipt;
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            receipt = (await scope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(CreateUploadRequest())).Receipt!;
            receipt.ShouldNotBeNull();
            await PutAdmittedUploadAsync(scope.Services, receipt);
        }

        UncertainReplacementObjectStorage selectedStorage = storage
            ?? throw new InvalidOperationException("The storage test wrapper was not registered.");
        Guid workId = await fixture.MaintenanceExecutionFindUploadWorkAsync(user.OrganizationId, receipt.OperationId);
        await fixture.MaintenanceExecutionMakeWorkDueAsync(workId);
        return new(user.OrganizationId, receipt, workId, environment, selectedStorage);
    }

    private static async Task<ObjectStorageRevision> AssertFencingRetainedAsync(ExpiredUploadWork work)
    {
        (await fixture.ReadUploadLifecycleAsync(work.OrganizationId, work.Receipt.OperationId)).ShouldBe(2);
        (await fixture.ReadUploadHoldStateAsync(work.OrganizationId, work.Receipt.OperationId)).ShouldBe(0);
        (await fixture.ReadQuotaMaintenanceWorkAsync(work.WorkId)).State.ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("organization_usage", work.OrganizationId)).ShouldBe(0);
        ConditionalObjectStat sentinel = await work.Storage.StatAsync(work.Receipt.ObjectKey);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        sentinel.Identity.ShouldBe(work.Receipt.Identity);
        sentinel.Revision.ShouldNotBeNull();
        return sentinel.Revision!;
    }

    private static async Task AssertCancelledUploadAsync(ExpiredUploadWork work)
    {
        (await fixture.ReadUploadLifecycleAsync(work.OrganizationId, work.Receipt.OperationId)).ShouldBe(3);
        (await fixture.ReadUploadHoldStateAsync(work.OrganizationId, work.Receipt.OperationId)).ShouldBe(2);
        (await fixture.ReadQuotaMaintenanceWorkAsync(work.WorkId)).State.ShouldBe(3);
        (await fixture.CountQuotaRowsAsync("organization_usage", work.OrganizationId)).ShouldBe(0);
        ConditionalObjectStat sentinel = await work.Storage.StatAsync(work.Receipt.ObjectKey);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        sentinel.Identity.ShouldBe(work.Receipt.Identity);
        sentinel.Revision.ShouldNotBeNull();

        using MemoryStream lateWriter = new("admitted module upload"u8.ToArray());
        (await work.Storage.TryCreateAsync(work.Receipt.ObjectKey, lateWriter, 22, "text/plain", work.Receipt.Identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await work.Storage.StatAsync(work.Receipt.ObjectKey)).Revision.ShouldBe(sentinel.Revision);
    }

    private sealed record ExpiredUploadWork(Guid OrganizationId, ModuleUploadReceipt Receipt, Guid WorkId,
        UploadAdmissionEnvironment Environment, UncertainReplacementObjectStorage Storage);

    private enum StorageFault { ReturnUnavailableAfterReplacement, ThrowAfterReplacement, FirstPostReplacementStatUnavailable }

    private sealed class UncertainReplacementObjectStorage(LocalObjectStorage inner, StorageFault fault) : IConditionalObjectStorage
    {
        private bool replacementCompleted;
        private bool returnedUnavailableStat;

        public int ReplacementCalls { get; private set; }
        public string NamespaceFingerprint => inner.NamespaceFingerprint;

        public Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default)
        {
            if (replacementCompleted && fault == StorageFault.FirstPostReplacementStatUnavailable && !returnedUnavailableStat)
            {
                returnedUnavailableStat = true;
                return Task.FromResult(new ConditionalObjectStat(ConditionalObjectState.Unavailable));
            }
            return inner.StatAsync(key, cancellationToken);
        }

        public Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length, string contentType,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default) =>
            inner.TryCreateAsync(key, content, length, contentType, identity, cancellationToken);

        public async Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key,
            ObjectStorageRevision? expectedVersion, ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
        {
            ReplacementCalls++;
            ConditionalObjectWriteResult result = await inner.TryReplaceWithCancellationAsync(
                key, expectedVersion, identity, cancellationToken);
            if (result.Disposition != ConditionalObjectWriteDisposition.Written) return result;

            replacementCompleted = true;
            return fault switch
            {
                StorageFault.ReturnUnavailableAfterReplacement => new(ConditionalObjectWriteDisposition.Unavailable),
                StorageFault.ThrowAfterReplacement => throw new IOException("Injected lost replacement response."),
                _ => result
            };
        }
    }
}
