using Trykatch.Modules;

namespace Trykatch.Modules.Documents.Domain;

public enum DocumentLifecycleState { Active = 1, Archived = 2, Deleted = 3 }

public sealed class DocumentRecord : IOrganizationOwned
{
    private DocumentRecord() { }

    private DocumentRecord(
        Guid id,
        Guid organizationId,
        Guid actorId,
        string title,
        string description,
        string fileName,
        string mediaType,
        long sizeBytes,
        string sha256,
        string objectKey,
        DateTimeOffset now,
        string documentType)
    {
        Id = id;
        OrganizationId = organizationId;
        CreatedBy = actorId;
        Title = title.Trim();
        Description = description.Trim();
        DocumentType = DocumentTypes.RequireValid(documentType);
        FileName = fileName;
        MediaType = mediaType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        ObjectKey = objectKey;
        CreatedAt = now;
    }

    public Guid Id { get; private init; }
    public Guid OrganizationId { get; private init; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string DocumentType { get; private set; } = DocumentTypes.Other;
    public string? FileName { get; private set; }
    public string? MediaType { get; private set; }
    public long? SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public string? ObjectKey { get; private set; }
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

    public static DocumentRecord CreateUpload(
        Guid id,
        Guid organizationId,
        Guid actorId,
        string title,
        string? description,
        string fileName,
        string mediaType,
        long sizeBytes,
        string sha256,
        string objectKey,
        DateTimeOffset now,
        string documentType = DocumentTypes.Other) =>
        new(id, organizationId, actorId, title, description ?? string.Empty, fileName, mediaType,
            sizeBytes, sha256, objectKey, now, documentType);

    public void UpdateMetadata(string title, string? description, DateTimeOffset now, string? documentType = null)
    {
        if (LifecycleState != DocumentLifecycleState.Active)
            throw new InvalidOperationException("Restore the document before editing it.");
        string validatedType = DocumentTypes.RequireValid(documentType ?? DocumentType);
        Title = title.Trim();
        Description = description?.Trim() ?? string.Empty;
        DocumentType = validatedType;
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
