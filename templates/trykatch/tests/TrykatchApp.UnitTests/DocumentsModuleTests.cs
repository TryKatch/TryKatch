using Shouldly;
using Trykatch.Modules.Documents;
using TrykatchApp.Modules;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class DocumentsModuleTests
{
    [TestMethod]
    public void DocumentDeletionIsReasonedRecoverableAndRequiresArchive()
    {
        Guid actorId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DocumentRecord document = DocumentRecord.Create(Guid.CreateVersion7(), actorId, "Plan", "First", now);

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
    public void DocumentContentUpdateChangesTypedMetadataTimestamp()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        DateTimeOffset updatedAt = createdAt.AddMinutes(5);
        DocumentRecord document = DocumentRecord.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Plan", "First", createdAt);

        document.Update("Revised plan", "Second version", updatedAt);

        document.Title.ShouldBe("Revised plan");
        document.Content.ShouldBe("Second version");
        document.UpdatedAt.ShouldBe(updatedAt);
    }

    [TestMethod]
    public async Task ApplicationUseCaseRepeatsManageAuthorizationBeforeWriting()
    {
        RecordingModuleData data = new();
        DocumentsUseCases useCases = new(data, new DeniedAuthorizer(), TimeProvider.System);

        DocumentOperationResult<DocumentDto> result = await useCases.CreateAsync(
            new("Blocked", "content"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Code.ShouldBe("forbidden");
        data.Added.ShouldBe(0);
        data.Saves.ShouldBe(0);
    }

    private sealed class DeniedAuthorizer : IModulePermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingModuleData : IOrganizationModuleData
    {
        public Guid OrganizationId { get; } = Guid.CreateVersion7();
        public Guid ActorId { get; } = Guid.CreateVersion7();
        public int Added { get; private set; }
        public int Saves { get; private set; }
        public IQueryable<TEntity> Query<TEntity>() where TEntity : class => Array.Empty<TEntity>().AsQueryable();
        public void Add<TEntity>(TEntity entity) where TEntity : class => Added++;
        public void Remove<TEntity>(TEntity entity) where TEntity : class { }
        public void RecordAudit(string action, string subjectType, string subjectId, string displayName,
            IReadOnlyDictionary<string, string?>? details = null) { }
        public void Enqueue<TMessage>(TMessage message) where TMessage : notnull { }
        public Task SaveChangesAsync(CancellationToken cancellationToken) { Saves++; return Task.CompletedTask; }
    }
}
