using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.UnitTests;

[TestClass]
public sealed class DocumentsUploadUseCaseTests
{
    [TestMethod]
    public async Task InvalidRequestDoesNotReserveOrTouchStorage()
    {
        Fixture fixture = new();
        using MemoryStream content = new([1]);
        UploadDocumentCommand command = Fixture.Command(content) with { Title = " " };

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(command, default);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe("validation");
        fixture.Admission.Requests.ShouldBeEmpty();
        fixture.Storage.WriteCount.ShouldBe(0);
        fixture.Events.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UploadReservesCanonicalIdentityEnlistsRollbackBeforeConditionalWriteAndFinalizesMetadata()
    {
        Fixture fixture = new();
        using MemoryStream content = new([1, 2, 3]);
        UploadDocumentCommand command = Fixture.Command(content) with
        {
            Title = "  Board minutes ",
            Description = " Q3 meeting ",
            FileName = "folder\\minutes.pdf",
            Sha256 = new string('a', 64),
            SizeBytes = 3,
            DocumentType = "report"
        };

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(command, default);

        result.IsSuccess.ShouldBeTrue();
        ModuleUploadRequest request = fixture.Admission.Requests.Single();
        request.Policy.ShouldBe(new ModuleUploadPolicyRef("documents", "documents.upload", 1));
        request.OperationId.ShouldNotBe(Guid.Empty);
        request.Length.ShouldBe(3);
        request.Sha256.ShouldBe(new string('a', 64));
        request.MetadataFingerprint.ShouldBe(DocumentUploadFingerprint.Compute(command));
        fixture.Events.ShouldBe(["reserve", "enlist", "write", "finalize", "add", "save"]);
        fixture.Storage.WriteCount.ShouldBe(1);
        fixture.RawStorage.PutCount.ShouldBe(0);
        fixture.RawStorage.DeleteCount.ShouldBe(0);
        fixture.Store.Added.ShouldNotBeNull();
        fixture.Store.Added!.UploadOperationId.ShouldBe(request.OperationId);
        fixture.Store.Added.OrganizationId.ShouldBe(fixture.OrganizationId);
        fixture.Store.Added.ObjectKey.ShouldBe(fixture.Admission.Receipt!.ObjectKey);
        fixture.Store.Added.FileName.ShouldBe("minutes.pdf");
        fixture.Store.Added.Sha256.ShouldBe(new string('A', 64));
        fixture.Context.Audits.ShouldContain(audit => audit.Action == "document.uploaded");
        fixture.Context.Events.Single().ShouldBeOfType<global::Trykatch.Modules.Documents.IntegrationEvents.DocumentChanged>();

        Func<CancellationToken, Task> rollback = fixture.Compensation.Callbacks.Single();
        await rollback(default);
        fixture.Admission.CancelCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task EmptyExplicitOperationIdIsRejectedBeforeReservation()
    {
        Fixture fixture = new();
        using MemoryStream content = new([1]);
        UploadDocumentCommand command = Fixture.Command(content) with { OperationId = Guid.Empty };

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(command, default);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe("validation");
        fixture.Admission.Requests.ShouldBeEmpty();
        fixture.Storage.WriteCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task ExactExistingProvisionalIdentityCanBeReusedButDifferentIdentityFailsClosed()
    {
        Fixture matching = new();
        using MemoryStream matchingContent = new([1]);
        matching.Storage.WriteResultFactory = (_, identity) => new(ConditionalObjectWriteDisposition.AlreadyExists,
            new(ConditionalObjectState.Provisional, identity.OriginalLength, identity, new("revision-1")));

        DocumentOperationResult<DocumentDto> accepted = await matching.UseCases.UploadAsync(
            Fixture.Command(matchingContent) with { OperationId = Guid.NewGuid() }, default);

        accepted.IsSuccess.ShouldBeTrue();
        matching.Store.Added.ShouldNotBeNull();

        Fixture foreign = new();
        using MemoryStream foreignContent = new([1]);
        foreign.Storage.WriteResultFactory = (_, identity) => new(ConditionalObjectWriteDisposition.AlreadyExists,
            new(ConditionalObjectState.Provisional, identity.OriginalLength,
                identity with { MetadataFingerprint = new string('f', 64) }, new("revision-2")));

        DocumentOperationResult<DocumentDto> rejected = await foreign.UseCases.UploadAsync(
            Fixture.Command(foreignContent) with { OperationId = Guid.NewGuid() }, default);

        rejected.IsSuccess.ShouldBeFalse();
        rejected.Code.ShouldNotBeNull();
        foreign.Store.Added.ShouldBeNull();
        foreign.Admission.FinalizeCount.ShouldBe(0);
        foreign.Admission.CancelCount.ShouldBe(0);

        foreach (ConditionalObjectState state in new[]
                 { ConditionalObjectState.Cancellation, ConditionalObjectState.Unowned, ConditionalObjectState.Unavailable, ConditionalObjectState.Missing })
        {
            Fixture unsafeState = new();
            using MemoryStream unsafeContent = new([1]);
            unsafeState.Storage.WriteResultFactory = (_, identity) => new(ConditionalObjectWriteDisposition.AlreadyExists,
                new(state, identity.OriginalLength, identity, new("revision-3")));

            DocumentOperationResult<DocumentDto> unsafeResult = await unsafeState.UseCases.UploadAsync(
                Fixture.Command(unsafeContent) with { OperationId = Guid.NewGuid() }, default);

            unsafeResult.IsSuccess.ShouldBeFalse();
            unsafeState.Admission.FinalizeCount.ShouldBe(0);
            unsafeState.Store.Added.ShouldBeNull();
        }
    }

    [TestMethod]
    public async Task StorageUnavailableOrInvalidPayloadDoesNotFinalizeOrCancelReservation()
    {
        foreach (ConditionalObjectWriteDisposition disposition in new[]
                 { ConditionalObjectWriteDisposition.Unavailable, ConditionalObjectWriteDisposition.InvalidPayload })
        {
            Fixture fixture = new();
            fixture.Storage.WriteResult = new(disposition);
            using MemoryStream content = new([1]);

            DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
                Fixture.Command(content) with { OperationId = Guid.NewGuid() }, default);

            result.IsSuccess.ShouldBeFalse();
            result.Code.ShouldNotBeNull();
            fixture.Admission.FinalizeCount.ShouldBe(0);
            fixture.Admission.CancelCount.ShouldBe(0);
            fixture.RawStorage.DeleteCount.ShouldBe(0);
        }
    }

    [TestMethod]
    public async Task AlreadyCommittedRetryLoadsAuthorizedCurrentDocumentWithoutReplayingUploadEffects()
    {
        Fixture fixture = new();
        Guid operationId = Guid.NewGuid();
        fixture.Admission.ReserveDisposition = ModuleUploadReserveDisposition.AlreadyCommitted;
        fixture.Admission.ReceiptSubjectId = Guid.NewGuid();
        fixture.Store.Current = fixture.Document(fixture.Admission.ReceiptSubjectId.Value, operationId);
        using MemoryStream content = new([1]);

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
            Fixture.Command(content) with { OperationId = operationId }, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Id.ShouldBe(fixture.Store.Current.Id);
        fixture.Store.LastIncludeRecoverable.ShouldBe(true);
        fixture.Storage.WriteCount.ShouldBe(0);
        fixture.Admission.FinalizeCount.ShouldBe(1);
        fixture.Admission.CallbackCount.ShouldBe(0);
        fixture.Store.SaveCount.ShouldBe(0);
        fixture.Context.Audits.ShouldBeEmpty();
        fixture.Context.Events.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task FinalizationCallbackFailureLeavesObjectForReconciliation()
    {
        Fixture fixture = new();
        fixture.Store.SaveFailure = new InvalidOperationException("metadata persistence failed");
        using MemoryStream content = new([1]);

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.UseCases.UploadAsync(
            Fixture.Command(content) with { OperationId = Guid.NewGuid() }, default));

        fixture.Storage.WriteCount.ShouldBe(1);
        fixture.RawStorage.DeleteCount.ShouldBe(0);
        fixture.Admission.CancelCount.ShouldBe(0);
        fixture.Compensation.Callbacks.Count.ShouldBe(1);
        fixture.Admission.CallbackCount.ShouldBe(1);
    }

    [TestMethod]
    [DataRow(ModuleUploadReserveDisposition.Denied, "upload_authority_denied", "forbidden")]
    [DataRow(ModuleUploadReserveDisposition.Denied, "quota_exhausted", "quota_exhausted")]
    [DataRow(ModuleUploadReserveDisposition.Denied, "quota_unconfigured", "conflict")]
    [DataRow(ModuleUploadReserveDisposition.Conflict, "upload_conflict", "conflict")]
    [DataRow(ModuleUploadReserveDisposition.Unavailable, "quota_busy", "unavailable")]
    [DataRow(ModuleUploadReserveDisposition.Unavailable, "quota_inventory_initializing", "unavailable")]
    public async Task AdmissionFailureCannotWriteOrReturnAPriorResult(
        ModuleUploadReserveDisposition disposition, string failureCode, string expectedCode)
    {
        Fixture fixture = new();
        fixture.Admission.ReserveDisposition = disposition;
        fixture.Admission.FailureCode = failureCode;
        using MemoryStream content = new([1]);

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(Fixture.Command(content), default);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe(expectedCode);
        result.Value.ShouldBeNull();
        fixture.Storage.WriteCount.ShouldBe(0);
        fixture.Admission.FinalizeCount.ShouldBe(0);
        fixture.Compensation.Callbacks.ShouldBeEmpty();
        fixture.Context.Audits.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ConcurrentCompletionAfterWriteReturnsCurrentDocumentWithoutRepeatingCallback()
    {
        Fixture fixture = new();
        Guid operationId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        fixture.Admission.ReceiptSubjectId = subjectId;
        fixture.Admission.AlreadyFinalized = true;
        fixture.Store.Current = fixture.Document(subjectId, operationId);
        using MemoryStream content = new([1]);

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
            Fixture.Command(content, operationId), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Title.ShouldBe("Current document");
        fixture.Storage.WriteCount.ShouldBe(1);
        fixture.Admission.CallbackCount.ShouldBe(0);
        fixture.Store.SaveCount.ShouldBe(0);
        fixture.Context.Audits.ShouldBeEmpty();
        fixture.Context.Events.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task AlreadyCommittedArchivedDocumentReturnsCurrentDtoWithoutReplayingEffects()
    {
        Fixture fixture = new();
        Guid operationId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        fixture.Admission.ReserveDisposition = ModuleUploadReserveDisposition.AlreadyCommitted;
        fixture.Admission.ReceiptSubjectId = subjectId;
        fixture.Store.Current = fixture.Document(subjectId, operationId);
        fixture.Store.Current.Archive(fixture.ActorId, DateTimeOffset.UtcNow);
        using MemoryStream content = new([1]);

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
            Fixture.Command(content) with { OperationId = operationId }, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Lifecycle.Status.ShouldBe(nameof(DocumentLifecycleState.Archived));
        fixture.Storage.WriteCount.ShouldBe(0);
        fixture.Admission.FinalizeCount.ShouldBe(1);
        fixture.Admission.CallbackCount.ShouldBe(0);
        fixture.Store.SaveCount.ShouldBe(0);
        fixture.Context.Audits.ShouldBeEmpty();
        fixture.Context.Events.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task AlreadyCommittedMissingOrDifferentOperationDocumentCannotBeRecreated()
    {
        foreach (DocumentRecord? document in new DocumentRecord?[]
                 { null, new Fixture().Document(Guid.NewGuid(), Guid.NewGuid()) })
        {
            Fixture fixture = new();
            Guid operationId = Guid.NewGuid();
            Guid subjectId = Guid.NewGuid();
            fixture.Admission.ReserveDisposition = ModuleUploadReserveDisposition.AlreadyCommitted;
            fixture.Admission.ReceiptSubjectId = subjectId;
            fixture.Store.Current = document;
            using MemoryStream content = new([1]);

            DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
                Fixture.Command(content) with { OperationId = operationId }, default);

            result.IsSuccess.ShouldBeFalse();
            result.Code.ShouldNotBeNull();
            fixture.Storage.WriteCount.ShouldBe(0);
            fixture.Store.Added.ShouldBeNull();
            fixture.Store.SaveCount.ShouldBe(0);
        }
    }

    [TestMethod]
    public async Task AlreadyCommittedPendingDeletionDocumentIsDeniedWithoutReplayingEffects()
    {
        Fixture fixture = new();
        Guid operationId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        fixture.Admission.ReserveDisposition = ModuleUploadReserveDisposition.AlreadyCommitted;
        fixture.Admission.ReceiptSubjectId = subjectId;
        fixture.Store.Current = fixture.Document(subjectId, operationId);
        fixture.Store.Current.Archive(fixture.ActorId, DateTimeOffset.UtcNow);
        fixture.Store.Current.RequestDeletion(fixture.ActorId, "Retention period complete", DateTimeOffset.UtcNow);
        using MemoryStream content = new([1]);

        DocumentOperationResult<DocumentDto> result = await fixture.UseCases.UploadAsync(
            Fixture.Command(content) with { OperationId = operationId }, default);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe("conflict");
        fixture.Storage.WriteCount.ShouldBe(0);
        fixture.Store.Added.ShouldBeNull();
        fixture.Store.SaveCount.ShouldBe(0);
        fixture.Context.Audits.ShouldBeEmpty();
        fixture.Context.Events.ShouldBeEmpty();
    }

    private sealed class Fixture
    {
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public Guid ActorId { get; } = Guid.NewGuid();
        public TestStore Store { get; } = new();
        public TestStorage Storage { get; }
        public TestRawStorage RawStorage { get; } = new();
        public TestAdmission Admission { get; }
        public TestCompensation Compensation { get; }
        public TestContext Context { get; }
        public List<string> Events { get; } = [];
        public DocumentsUseCases UseCases { get; }

        public Fixture()
        {
            Admission = new TestAdmission(Events);
            Compensation = new TestCompensation(Events);
            Context = new TestContext(OrganizationId, ActorId);
            Storage = new TestStorage(Events);
            Store.Events = Events;
            UseCases = new DocumentsUseCases(Store, RawStorage, Compensation, Context,
                new TestAuthorizer(), TimeProvider.System, Storage, Admission);
        }

        public static UploadDocumentCommand Command(Stream content, Guid? operationId = null) =>
            new("Board minutes", "Quarterly meeting", "minutes.pdf", "application/pdf", 1,
                new string('A', 64), content, "other", operationId);

        public DocumentRecord Document(Guid subjectId, Guid operationId) => DocumentRecord.CreateUpload(
            subjectId, OrganizationId, ActorId, "Current document", null, "minutes.pdf", "application/pdf", 1,
            new string('A', 64), Admission.ReceiptObjectKey, DateTimeOffset.UtcNow, uploadOperationId: operationId);
    }

    private sealed class TestAdmission(List<string> events) : IModuleUploadAdmission
    {
        private readonly Guid _subjectId = Guid.NewGuid();
        public List<ModuleUploadRequest> Requests { get; } = [];
        public ModuleUploadReserveDisposition ReserveDisposition { get; set; } = ModuleUploadReserveDisposition.Reserved;
        public Guid? ReceiptSubjectId { get; set; }
        public string ReceiptObjectKey { get; } = "_module-uploads/v1/test/documents/test/test";
        public TestReceipt? Receipt { get; private set; }
        public int FinalizeCount { get; private set; }
        public int CallbackCount { get; private set; }
        public int CancelCount { get; private set; }
        public bool AlreadyFinalized { get; set; }
        public string? FailureCode { get; set; }

        public Task<ModuleUploadReserveResult> ReserveAsync(ModuleUploadRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("reserve");
            Requests.Add(request);
            Guid subjectId = ReceiptSubjectId ?? _subjectId;
            Receipt = new TestReceipt(request.Policy, request.OperationId, subjectId,
                ReceiptObjectKey,
                new(request.OperationId, request.Length, request.Sha256, request.MetadataFingerprint));
            return Task.FromResult(new ModuleUploadReserveResult(ReserveDisposition, Receipt, FailureCode));
        }

        public async Task<ModuleUploadFinalization<T>> FinalizeAsync<T>(ModuleUploadReceipt receipt,
            Func<CancellationToken, Task<T>> finalize, CancellationToken cancellationToken = default)
        {
            events.Add("finalize");
            FinalizeCount++;
            if (ReserveDisposition == ModuleUploadReserveDisposition.AlreadyCommitted || AlreadyFinalized)
                return new(ModuleUploadFinalizationDisposition.AlreadyCommitted, default, receipt.SubjectId);
            CallbackCount++;
            T value = await finalize(cancellationToken);
            return new(ModuleUploadFinalizationDisposition.Committed, value, receipt.SubjectId);
        }

        public Task<ModuleUploadCancelResult> CancelAsync(ModuleUploadReceipt receipt, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            events.Add("cancel");
            return Task.FromResult(new ModuleUploadCancelResult(ModuleUploadCancelDisposition.Cancelled));
        }
    }

    private sealed class TestReceipt(ModuleUploadPolicyRef policy, Guid operationId, Guid subjectId,
        string objectKey, ObjectWriteIdentity identity) : ModuleUploadReceipt
    {
        public override ModuleUploadPolicyRef Policy { get; } = policy;
        public override Guid OperationId { get; } = operationId;
        public override Guid SubjectId { get; } = subjectId;
        public override string ObjectKey { get; } = objectKey;
        public override ObjectWriteIdentity Identity { get; } = identity;
        public override DateTimeOffset ExpiresAtUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private sealed class TestStorage(List<string> events) : IConditionalObjectStorage
    {
        public string NamespaceFingerprint { get; } = new string('b', 64);
        public int WriteCount { get; private set; }
        public ConditionalObjectWriteResult? WriteResult { get; set; }
        public Func<string, ObjectWriteIdentity, ConditionalObjectWriteResult>? WriteResultFactory { get; set; }

        public Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length, string contentType,
            ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            events.Add("write");
            return Task.FromResult(WriteResultFactory?.Invoke(key, identity) ?? WriteResult
                ?? new ConditionalObjectWriteResult(ConditionalObjectWriteDisposition.Written));
        }

        public Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConditionalObjectStat(ConditionalObjectState.Missing));

        public Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key,
            ObjectStorageRevision? expectedVersion, ObjectWriteIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConditionalObjectWriteResult(ConditionalObjectWriteDisposition.Written));
    }

    private sealed class TestRawStorage : IObjectStorage
    {
        public int PutCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Task PutAsync(string key, Stream content, long contentLength, string contentType,
            CancellationToken cancellationToken = default)
        {
            PutCount++;
            return Task.CompletedTask;
        }
        public Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(Stream.Null);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestStore : IDocumentStore
    {
        public List<string>? Events { get; set; }
        public DocumentRecord? Added { get; private set; }
        public DocumentRecord? Current { get; set; }
        public bool? LastIncludeRecoverable { get; private set; }
        public int SaveCount { get; private set; }
        public Exception? SaveFailure { get; set; }

        public Task<IReadOnlyList<DocumentRecord>> ListAsync(DocumentQueryScope scope, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentRecord>>([]);

        public Task<DocumentRecord?> FindAsync(Guid id, bool includeRecoverable, CancellationToken cancellationToken)
        {
            LastIncludeRecoverable = includeRecoverable;
            return Task.FromResult(Current?.Id == id ? Current : null);
        }

        public void Add(DocumentRecord document)
        {
            Events!.Add("add");
            Added = document;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Events!.Add("save");
            SaveCount++;
            if (SaveFailure is not null) throw SaveFailure;
            return Task.CompletedTask;
        }
    }

    private sealed class TestContext(Guid organizationId, Guid actorId) : IOrganizationModuleData
    {
        public Guid OrganizationId { get; } = organizationId;
        public Guid ActorId { get; } = actorId;
        public List<(string Action, string SubjectType, string SubjectId, string DisplayName)> Audits { get; } = [];
        public List<object> Events { get; } = [];
        public IQueryable<TEntity> Query<TEntity>() where TEntity : class => Array.Empty<TEntity>().AsQueryable();
        public void Add<TEntity>(TEntity entity) where TEntity : class { }
        public void Remove<TEntity>(TEntity entity) where TEntity : class { }
        public void RecordAudit(string action, string subjectType, string subjectId, string displayName,
            IReadOnlyDictionary<string, string?>? details = null) => Audits.Add((action, subjectType, subjectId, displayName));
        public void Enqueue<TMessage>(TMessage message) where TMessage : notnull => Events.Add(message);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestAuthorizer : IModulePermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestCompensation(List<string> events) : IModuleTransactionCompensation
    {
        public List<Func<CancellationToken, Task>> Callbacks { get; } = [];
        public void EnlistRollback(Func<CancellationToken, Task> compensation)
        {
            events.Add("enlist");
            Callbacks.Add(compensation);
        }
    }
}
