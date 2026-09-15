using Trykatch.Modules;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.IntegrationEvents;

namespace Trykatch.Modules.Documents.Application;

public sealed record UploadDocumentCommand(
    string Title,
    string? Description,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    Stream Content,
    string? DocumentType = null);
public sealed record UpdateDocumentCommand(string Title, string? Description, string? DocumentType = null);
public sealed record DocumentMetadataDto(DateTimeOffset? UpdatedAt, string MediaType, long SizeBytes, string Sha256);
public sealed record DocumentLifecycleDto(string Status, DateTimeOffset? ArchivedAt, Guid? ArchivedBy,
    DateTimeOffset? DeletedAt, Guid? DeletedBy, string? DeletionReason);
public sealed record DocumentDto(Guid Id, string Title, string Description, string FileName, DateTimeOffset CreatedAt,
    DocumentMetadataDto Metadata, DocumentLifecycleDto Lifecycle, string DocumentType);
public sealed record DocumentDownload(Stream Content, string FileName, string MediaType, long SizeBytes);

public sealed record DocumentOperationResult<T>(bool IsSuccess, T? Value, string? Code, string? Error);

public static class DocumentOperation
{
    public static DocumentOperationResult<T> Success<T>(T value) => new(true, value, null, null);
    public static DocumentOperationResult<T> Failure<T>(string code, string error) => new(false, default, code, error);
}

public static class DocumentUploadPolicy
{
    public const long MaximumBytes = 25 * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumBytes + (64 * 1024);

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/webp",
        "text/csv",
        "text/plain",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    public static string? Validate(UploadDocumentCommand command)
    {
        string? metadataError = ValidateMetadata(command.Title, command.Description, command.DocumentType);
        if (metadataError is not null) return metadataError;
        if (command.SizeBytes is <= 0 or > MaximumBytes)
            return "Choose a non-empty file no larger than 25 MB.";
        if (!AllowedMediaTypes.Contains(command.MediaType))
            return "This file type is not supported. Upload PDF, Office, text, CSV, JPEG, PNG, or WebP files.";
        string safeFileName = SafeFileName(command.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName.Length > 255
            || safeFileName.Contains('\r') || safeFileName.Contains('\n'))
            return "File name is required and cannot exceed 255 characters.";
        if (command.Sha256.Length != 64 || command.Sha256.Any(character => !Uri.IsHexDigit(character)))
            return "The file checksum is invalid.";
        if (!command.Content.CanRead) return "The uploaded file cannot be read.";
        return null;
    }

    public static string? ValidateMetadata(string title, string? description, string? documentType = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200)
            return "Title is required and cannot exceed 200 characters.";
        if (description?.Trim().Length > 2_000)
            return "Description cannot exceed 2,000 characters.";
        if (documentType is not null && !DocumentTypes.IsValid(documentType))
            return "Choose a supported document type.";
        return null;
    }

    public static string SafeFileName(string fileName)
    {
        string normalized = fileName.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..].Trim();
    }
}

public enum DocumentQueryScope { Active, Recoverable }

/// <summary>Persistence seam for document metadata use cases.</summary>
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
    IObjectStorage objectStorage,
    IModuleTransactionCompensation transactionCompensation,
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

    public async Task<DocumentOperationResult<DocumentDto>> UploadAsync(
        UploadDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = DocumentUploadPolicy.Validate(command);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);

        Guid id = Guid.CreateVersion7();
        string safeFileName = DocumentUploadPolicy.SafeFileName(command.FileName);
        string objectKey = $"organizations/{context.OrganizationId:N}/documents/{id:N}";
        DocumentRecord document = DocumentRecord.CreateUpload(
            id, context.OrganizationId, context.ActorId, command.Title, command.Description,
            safeFileName, command.MediaType, command.SizeBytes, command.Sha256.ToUpperInvariant(),
            objectKey, timeProvider.GetUtcNow(), command.DocumentType ?? DocumentTypes.Other);

        await objectStorage.PutAsync(objectKey, command.Content, command.SizeBytes, command.MediaType, cancellationToken);
        try
        {
            store.Add(document);
            RecordChange(document, "uploaded");
            await store.SaveChangesAsync(cancellationToken);
            transactionCompensation.EnlistRollback(
                compensationToken => objectStorage.DeleteAsync(objectKey, compensationToken));
        }
        catch
        {
            await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            throw;
        }
        return DocumentOperation.Success(ToDto(document));
    }

    public async Task<DocumentOperationResult<DocumentDownload>> DownloadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("documents.read", cancellationToken))
            return Forbidden<DocumentDownload>();
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: false, cancellationToken);
        if (document is null) return NotFound<DocumentDownload>();
        if (document.ObjectKey is null || document.FileName is null || document.MediaType is null || document.SizeBytes is null)
            return DocumentOperation.Failure<DocumentDownload>("conflict", "This legacy document does not contain an uploaded file.");
        Stream content = await objectStorage.GetAsync(document.ObjectKey, cancellationToken);
        return DocumentOperation.Success<DocumentDownload>(
            new(content, document.FileName, document.MediaType, document.SizeBytes.Value));
    }

    public async Task<DocumentOperationResult<DocumentDto>> UpdateAsync(
        Guid id,
        UpdateDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = DocumentUploadPolicy.ValidateMetadata(command.Title, command.Description, command.DocumentType);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);
        DocumentRecord? document = await store.FindAsync(id, includeRecoverable: false, cancellationToken);
        if (document is null) return NotFound<DocumentDto>();
        document.UpdateMetadata(command.Title, command.Description, timeProvider.GetUtcNow(), command.DocumentType);
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

    private Task<bool> CanManage(CancellationToken cancellationToken) =>
        authorizer.HasPermissionAsync("documents.manage", cancellationToken);

    private void RecordChange(DocumentRecord document, string operation)
    {
        IReadOnlyDictionary<string, string?>? details = operation == "deleted"
            ? new Dictionary<string, string?> { ["reasonProvided"] = "True" }
            : operation == "uploaded"
                ? new Dictionary<string, string?>
                {
                    ["fileName"] = document.FileName,
                    ["mediaType"] = document.MediaType,
                    ["sizeBytes"] = document.SizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
                : null;
        context.RecordAudit($"document.{operation}", "Document", document.Id.ToString(), document.Title, details);
        context.Enqueue(new DocumentChanged(document.Id, document.OrganizationId, operation, context.ActorId,
            timeProvider.GetUtcNow(), document.DeletionReason));
    }

    private static DocumentOperationResult<T> Forbidden<T>() =>
        DocumentOperation.Failure<T>("forbidden", "Documents cannot be changed by this membership.");
    private static DocumentOperationResult<T> NotFound<T>() =>
        DocumentOperation.Failure<T>("not_found", "Document was not found.");

    public static DocumentDto ToDto(DocumentRecord document) => new(
        document.Id,
        document.Title,
        document.Description,
        document.FileName ?? "Legacy text document",
        document.CreatedAt,
        new(document.UpdatedAt, document.MediaType ?? "text/plain", document.SizeBytes ?? 0, document.Sha256 ?? string.Empty),
        new(document.LifecycleState.ToString(), document.ArchivedAt, document.ArchivedBy,
            document.DeletedAt, document.DeletedBy, document.DeletionReason),
        document.DocumentType);
}
