using Trykatch.Modules;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.IntegrationEvents;

namespace Trykatch.Modules.Documents.Application;

public sealed record SaveDocumentCommand(string Title, string? Content);
public sealed record DocumentMetadataDto(DateTimeOffset? UpdatedAt, string MediaType, int CharacterCount);
public sealed record DocumentLifecycleDto(string Status, DateTimeOffset? ArchivedAt, Guid? ArchivedBy,
    DateTimeOffset? DeletedAt, Guid? DeletedBy, string? DeletionReason);
public sealed record DocumentDto(Guid Id, string Title, string Content, DateTimeOffset CreatedAt,
    DocumentMetadataDto Metadata, DocumentLifecycleDto Lifecycle);

public sealed record DocumentOperationResult<T>(bool IsSuccess, T? Value, string? Code, string? Error);

public static class DocumentOperation
{
    public static DocumentOperationResult<T> Success<T>(T value) => new(true, value, null, null);
    public static DocumentOperationResult<T> Failure<T>(string code, string error) => new(false, default, code, error);
}

public enum DocumentQueryScope
{
    Active,
    Recoverable
}

/// <summary>Persistence seam for document use cases.</summary>
public interface IDocumentStore
{
    Task<IReadOnlyList<DocumentRecord>> ListAsync(DocumentQueryScope scope, CancellationToken cancellationToken);
    Task<DocumentRecord?> FindAsync(Guid id, bool includeRecoverable, CancellationToken cancellationToken);
    void Add(DocumentRecord document);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The module's application interface; adapters translate its typed results.</summary>
public sealed class DocumentsUseCases(
    IDocumentStore store,
    IOrganizationModuleData context,
    IModulePermissionAuthorizer authorizer,
    TimeProvider timeProvider)
{
    public async Task<DocumentOperationResult<DocumentDto[]>> ListAsync(string lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("documents.read", cancellationToken))
            return DocumentOperation.Failure<DocumentDto[]>("forbidden", "Documents cannot be viewed by this membership.");
        DocumentQueryScope scope;
        if (string.Equals(lifecycle, "recoverable", StringComparison.OrdinalIgnoreCase))
            scope = DocumentQueryScope.Recoverable;
        else if (string.Equals(lifecycle, "active", StringComparison.OrdinalIgnoreCase))
            scope = DocumentQueryScope.Active;
        else
            return DocumentOperation.Failure<DocumentDto[]>("validation", "Lifecycle must be active or recoverable.");
        IReadOnlyList<DocumentRecord> records = await store.ListAsync(scope, cancellationToken);
        return DocumentOperation.Success(records.Select(ToDto).ToArray());
    }

    public async Task<DocumentOperationResult<DocumentDto>> CreateAsync(SaveDocumentCommand command, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = Validate(command);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);
        DocumentRecord document = DocumentRecord.Create(context.OrganizationId, context.ActorId, command.Title, command.Content, timeProvider.GetUtcNow());
        store.Add(document);
        RecordChange(document, "created");
        await store.SaveChangesAsync(cancellationToken);
        return DocumentOperation.Success(ToDto(document));
    }

    public async Task<DocumentOperationResult<DocumentDto>> UpdateAsync(Guid id, SaveDocumentCommand command, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = Validate(command);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: false, cancellationToken);
        if (document is null) return NotFound<DocumentDto>();
        document.Update(command.Title, command.Content, timeProvider.GetUtcNow());
        RecordChange(document, "updated");
        await store.SaveChangesAsync(cancellationToken);
        return DocumentOperation.Success(ToDto(document));
    }

    public async Task<DocumentOperationResult<bool>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: false, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.Archive(context.ActorId, timeProvider.GetUtcNow()))
        {
            RecordChange(document, "archived");
            await store.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    public async Task<DocumentOperationResult<bool>> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: true, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.Restore())
        {
            RecordChange(document, "restored");
            await store.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    public async Task<DocumentOperationResult<bool>> DeleteAsync(Guid id, string? requestedReason, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        string reason = requestedReason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
            return DocumentOperation.Failure<bool>("validation", "A deletion reason containing 10-500 characters is required.");
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: true, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.LifecycleState != DocumentLifecycleState.Archived)
            return DocumentOperation.Failure<bool>("conflict", "Archive the document before requesting deletion.");
        if (document.RequestDeletion(context.ActorId, reason, timeProvider.GetUtcNow()))
        {
            RecordChange(document, "deleted");
            await store.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    private Task<bool> CanManage(CancellationToken cancellationToken) => authorizer.HasPermissionAsync("documents.manage", cancellationToken);

    private void RecordChange(DocumentRecord document, string operation)
    {
        IReadOnlyDictionary<string, string?>? details = operation == "deleted"
            ? new Dictionary<string, string?> { ["reasonProvided"] = "True" }
            : null;
        context.RecordAudit($"document.{operation}", "Document", document.Id.ToString(), document.Title, details);
        context.Enqueue(new DocumentChanged(document.Id, document.OrganizationId, operation, context.ActorId,
            timeProvider.GetUtcNow(), document.DeletionReason));
    }

    private static string? Validate(SaveDocumentCommand command) =>
        string.IsNullOrWhiteSpace(command.Title) || command.Title.Trim().Length > 200
            ? "Title is required and cannot exceed 200 characters." : null;
    private static DocumentOperationResult<T> Forbidden<T>() =>
        DocumentOperation.Failure<T>("forbidden", "Documents cannot be changed by this membership.");
    private static DocumentOperationResult<T> NotFound<T>() =>
        DocumentOperation.Failure<T>("not_found", "Document was not found.");

    private static DocumentDto ToDto(DocumentRecord document) => new(
        document.Id, document.Title, document.Content, document.CreatedAt,
        new(document.UpdatedAt, "text/plain", document.Content.Length),
        new(document.LifecycleState.ToString(), document.ArchivedAt, document.ArchivedBy,
            document.DeletedAt, document.DeletedBy, document.DeletionReason));
}
