using Trykatch.Modules;

namespace Trykatch.Modules.Documents.Domain;

public enum DocumentLifecycleState { Active = 1, Archived = 2, Deleted = 3 }

public sealed class DocumentRecord : IOrganizationOwned
{
    private DocumentRecord() { }

    private DocumentRecord(Guid organizationId, Guid actorId, string title, string content, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        CreatedBy = actorId;
        Title = title.Trim();
        Content = content.Trim();
        CreatedAt = now;
    }

    public Guid Id { get; private init; }
    public Guid OrganizationId { get; private init; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public Guid CreatedBy { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedBy { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }
    public DocumentLifecycleState LifecycleState => DeletedAt is not null
        ? DocumentLifecycleState.Deleted
        : ArchivedAt is not null ? DocumentLifecycleState.Archived : DocumentLifecycleState.Active;

    public static DocumentRecord Create(Guid organizationId, Guid actorId, string title, string? content, DateTimeOffset now) =>
        new(organizationId, actorId, title, content ?? string.Empty, now);

    public void Update(string title, string? content, DateTimeOffset now)
    {
        if (LifecycleState != DocumentLifecycleState.Active)
            throw new InvalidOperationException("Restore the document before editing it.");
        Title = title.Trim();
        Content = content?.Trim() ?? string.Empty;
        UpdatedAt = now;
    }

    public bool Archive(Guid actorId, DateTimeOffset now)
    {
        if (DeletedAt is not null) throw new InvalidOperationException("A deleted document cannot be archived.");
        if (ArchivedAt is not null) return false;
        ArchivedAt = now;
        ArchivedBy = actorId;
        return true;
    }

    public bool Restore()
    {
        if (LifecycleState == DocumentLifecycleState.Active) return false;
        ArchivedAt = null;
        ArchivedBy = null;
        DeletedAt = null;
        DeletedBy = null;
        DeletionReason = null;
        return true;
    }

    public bool RequestDeletion(Guid actorId, string reason, DateTimeOffset now)
    {
        if (LifecycleState != DocumentLifecycleState.Archived)
            throw new InvalidOperationException("The document must be archived before deletion can be requested.");
        if (reason.Length is < 10 or > 500)
            throw new ArgumentException("A deletion reason containing 10-500 characters is required.", nameof(reason));
        DeletedAt = now;
        DeletedBy = actorId;
        DeletionReason = reason;
        return true;
    }
}
