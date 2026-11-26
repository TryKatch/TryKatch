using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class DocumentsModuleTests
{
    [TestMethod]
    public void DocumentDeletionIsReasonedRecoverableAndRequiresArchive()
    {
        Guid actorId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DocumentRecord document = CreateDocument(actorId, "Plan", "First", now);

        Should.Throw<InvalidOperationException>(() =>
            document.RequestDeletion(actorId, "No longer needed", now.AddMinutes(1)));
        document.Archive(actorId, now.AddMinutes(2)).ShouldBeTrue();
        document.RequestDeletion(actorId, "No longer needed", now.AddMinutes(3)).ShouldBeTrue();

        document.LifecycleState.ShouldBe(DocumentLifecycleState.Deleted);
        document.DeletionReason.ShouldBe("No longer needed");
        document.Restore().ShouldBeTrue();
        document.LifecycleState.ShouldBe(DocumentLifecycleState.Active);
        document.DeletionReason.ShouldBeNull();
    }

    [TestMethod]
    public void DocumentMetadataUpdateChangesTypedMetadataTimestamp()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        DateTimeOffset updatedAt = createdAt.AddMinutes(5);
        DocumentRecord document = CreateDocument(
            Guid.CreateVersion7(), "Plan", "First", createdAt);

        document.UpdateMetadata("Revised plan", "Second version", updatedAt);

        document.Title.ShouldBe("Revised plan");
        document.Description.ShouldBe("Second version");
        document.UpdatedAt.ShouldBe(updatedAt);
    }

    [TestMethod]
    public void UploadPolicyRejectsAPathWithoutAFileName()
    {
        using MemoryStream content = new([1]);
        string? error = DocumentUploadPolicy.Validate(new(
            "Runbook", null, "../../", "application/pdf", content.Length, new string('A', 64), content));

        error.ShouldBe("File name is required and cannot exceed 255 characters.");
    }

    [TestMethod]
    public async Task ApplicationUseCaseRepeatsManageAuthorizationBeforeWriting()
    {
        RecordingModuleData data = new();
        RecordingDocumentStore store = new();
        RecordingConditionalObjectStorage conditionalStorage = new();
        RecordingUploadAdmission admission = new(data);
        DocumentsUseCases useCases = new(
            store, new RecordingObjectStorage(), new RecordingTransactionCompensation(),
            data, new DeniedAuthorizer(), TimeProvider.System, conditionalStorage, admission);

        await using MemoryStream content = new([1, 2, 3]);
        DocumentOperationResult<DocumentDto> result = await useCases.UploadAsync(
            new("Blocked", "content", "blocked.pdf", "application/pdf", content.Length,
                new string('A', 64), content), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe("forbidden");
        store.Added.ShouldBe(0);
        store.Saves.ShouldBe(0);
        admission.ReserveCount.ShouldBe(0);
        conditionalStorage.Writes.ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadFencesAfterMetadataFailureOnlyWhenTheTransactionRollsBack()
    {
        RecordingModuleData data = new();
        RecordingDocumentStore store = new() { FailOnSave = true };
        RecordingObjectStorage storage = new();
        RecordingConditionalObjectStorage conditionalStorage = new();
        RecordingUploadAdmission admission = new(data);
        RecordingTransactionCompensation compensation = new();
        DocumentsUseCases useCases = new(store, storage, compensation,
            data, new AllowedAuthorizer(), TimeProvider.System, conditionalStorage, admission);

        await using MemoryStream content = new([1, 2, 3]);
        await Should.ThrowAsync<InvalidOperationException>(() => useCases.UploadAsync(
            new("Runbook", "Recovery steps", "../../runbook.pdf", "application/pdf", content.Length,
                new string('A', 64), content), CancellationToken.None));

        conditionalStorage.Writes.ShouldBe(1);
        conditionalStorage.LastKey.ShouldBe(admission.Receipt!.ObjectKey);
        storage.Puts.ShouldBe(0);
        storage.Deletes.ShouldBe(0);
        admission.CancelCount.ShouldBe(0);

        await compensation.RollbackAsync();

        admission.CancelCount.ShouldBe(1);
        storage.Deletes.ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadFencesTheAdmittedObjectWhenTheRequestTransactionRollsBack()
    {
        RecordingModuleData data = new();
        RecordingDocumentStore store = new();
        RecordingObjectStorage storage = new();
        RecordingTransactionCompensation compensation = new();
        RecordingConditionalObjectStorage conditionalStorage = new();
        RecordingUploadAdmission admission = new(data);
        DocumentsUseCases useCases = new(
            store, storage, compensation, data, new AllowedAuthorizer(), TimeProvider.System,
            conditionalStorage, admission);

        await using MemoryStream content = new([1, 2, 3]);
        DocumentOperationResult<DocumentDto> result = await useCases.UploadAsync(
            new("Runbook", "Recovery steps", "runbook.pdf", "application/pdf", content.Length,
                new string('A', 64), content), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        conditionalStorage.Writes.ShouldBe(1);
        conditionalStorage.LastKey.ShouldBe(admission.Receipt!.ObjectKey);
        conditionalStorage.LastIdentity.ShouldBe(admission.Receipt.Identity);
        storage.Puts.ShouldBe(0);
        storage.Deletes.ShouldBe(0);
        admission.CancelCount.ShouldBe(0);
        await compensation.RollbackAsync();
        admission.CancelCount.ShouldBe(1);
        storage.Puts.ShouldBe(0);
        storage.Deletes.ShouldBe(0);
    }

    private static DocumentRecord CreateDocument(
        Guid actorId,
        string title,
        string description,
        DateTimeOffset now) =>
        DocumentRecord.CreateUpload(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            actorId,
            title,
            description,
            "document.pdf",
            "application/pdf",
            3,
            new string('A', 64),
            $"organizations/test/documents/{Guid.CreateVersion7():N}",
            now);

    private sealed class DeniedAuthorizer : IModulePermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class AllowedAuthorizer : IModulePermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingModuleData : IOrganizationModuleData
    {
        public Guid OrganizationId { get; } = Guid.CreateVersion7();
        public Guid ActorId { get; } = Guid.CreateVersion7();
        public IQueryable<TEntity> Query<TEntity>() where TEntity : class => Array.Empty<TEntity>().AsQueryable();
        public void Add<TEntity>(TEntity entity) where TEntity : class { }
        public void Remove<TEntity>(TEntity entity) where TEntity : class { }
        public void RecordAudit(string action, string subjectType, string subjectId, string displayName,
            IReadOnlyDictionary<string, string?>? details = null) { }
        public void Enqueue<TMessage>(TMessage message) where TMessage : notnull { }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public int Puts { get; private set; }
        public int Deletes { get; private set; }
        public string LastKey { get; private set; } = string.Empty;

        public Task PutAsync(string key, Stream content, long contentLength, string contentType,
            CancellationToken cancellationToken = default)
        {
            Puts++;
            LastKey = key;
            return Task.CompletedTask;
        }
        public Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Deletes++;
            LastKey = key;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConditionalObjectStorage : IConditionalObjectStorage
    {
        public string NamespaceFingerprint { get; } = new('b', 64);
        public int Writes { get; private set; }
        public string? LastKey { get; private set; }
        public ObjectWriteIdentity? LastIdentity { get; private set; }

        public Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length,
            string contentType, ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
        {
            Writes++;
            LastKey = key;
            LastIdentity = identity;
            return Task.FromResult(new ConditionalObjectWriteResult(ConditionalObjectWriteDisposition.Written));
        }

        public Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConditionalObjectStat(ConditionalObjectState.Missing));

        public Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key,
            ObjectStorageRevision? expectedVersion, ObjectWriteIdentity identity,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConditionalObjectWriteResult(ConditionalObjectWriteDisposition.Written));
    }

    private sealed class RecordingUploadAdmission(RecordingModuleData data) : IModuleUploadAdmission
    {
        public int ReserveCount { get; private set; }
        public int CancelCount { get; private set; }
        public RecordingUploadReceipt? Receipt { get; private set; }

        public Task<ModuleUploadReserveResult> ReserveAsync(ModuleUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            ReserveCount++;
            Receipt = new RecordingUploadReceipt(request.Policy, request.OperationId, Guid.CreateVersion7(),
                $"_module-uploads/v1/{data.OrganizationId:N}/{request.OperationId:N}",
                new(request.OperationId, request.Length, request.Sha256, request.MetadataFingerprint));
            return Task.FromResult(new ModuleUploadReserveResult(ModuleUploadReserveDisposition.Reserved, Receipt));
        }

        public async Task<ModuleUploadFinalization<T>> FinalizeAsync<T>(ModuleUploadReceipt receipt,
            Func<CancellationToken, Task<T>> finalize, CancellationToken cancellationToken = default) =>
            new(ModuleUploadFinalizationDisposition.Committed, await finalize(cancellationToken), receipt.SubjectId);

        public Task<ModuleUploadCancelResult> CancelAsync(ModuleUploadReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.FromResult(new ModuleUploadCancelResult(ModuleUploadCancelDisposition.Cancelled));
        }
    }

    private sealed class RecordingUploadReceipt(ModuleUploadPolicyRef policy, Guid operationId, Guid subjectId,
        string objectKey, ObjectWriteIdentity identity) : ModuleUploadReceipt
    {
        public override ModuleUploadPolicyRef Policy { get; } = policy;
        public override Guid OperationId { get; } = operationId;
        public override Guid SubjectId { get; } = subjectId;
        public override string ObjectKey { get; } = objectKey;
        public override ObjectWriteIdentity Identity { get; } = identity;
        public override DateTimeOffset ExpiresAtUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
    }

    private sealed class RecordingTransactionCompensation : IModuleTransactionCompensation
    {
        private readonly List<Func<CancellationToken, Task>> callbacks = [];

        public void EnlistRollback(Func<CancellationToken, Task> compensation) => callbacks.Add(compensation);

        public async Task RollbackAsync()
        {
            foreach (Func<CancellationToken, Task> callback in callbacks.AsEnumerable().Reverse())
                await callback(CancellationToken.None);
            callbacks.Clear();
        }
    }

    private sealed class RecordingDocumentStore : IDocumentStore
    {
        public int Added { get; private set; }
        public int Saves { get; private set; }
        public bool FailOnSave { get; init; }

        public Task<IReadOnlyList<DocumentRecord>> ListAsync(
            DocumentQueryScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentRecord>>([]);

        public Task<DocumentRecord?> FindAsync(
            Guid id,
            bool includeRecoverable,
            CancellationToken cancellationToken) =>
            Task.FromResult<DocumentRecord?>(null);

        public void Add(DocumentRecord document) => Added++;

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            if (FailOnSave) throw new InvalidOperationException("metadata persistence failed");
            return Task.CompletedTask;
        }
    }
}
