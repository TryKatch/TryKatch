using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TrykatchApp.Modules;
using TrykatchApp.Modules.AspNetCore;

namespace Trykatch.Modules.Documents;

public sealed class DocumentsModule : ITrykatchModule, ITrykatchOrganizationEndpointContributor, ITrykatchModuleMigrationContributor
{
    public string ModuleId => Descriptor.Id;

    public TrykatchModuleDescriptor Descriptor { get; } = new(
        "documents", "Documents", "1.0.0",
        "Organization-owned document records supplied by an independently packaged full-stack module.",
        ["projects"], [],
        TrykatchModuleCapabilities.Api | TrykatchModuleCapabilities.Web | TrykatchModuleCapabilities.Data
            | TrykatchModuleCapabilities.BackgroundWork | TrykatchModuleCapabilities.Assistant,
        [new("documents.list.after-table", "Renders module contributions after the documents table.", TrykatchExtensionPointKind.UiSlot, TrykatchModuleCapabilities.Web)])
    {
        DefaultDataOwnership = TrykatchDataOwnership.Organization,
        DataResources =
        [
            new("documents", "app", "documents", TrykatchDataOwnership.Organization,
                typeof(DocumentRecord).FullName, "documents_organization_isolation")
        ],
        Permissions =
        [
            new("documents.read", "View documents", "View documents in the current workspace.", DefaultRoles: ["admin", "member", "viewer"]),
            new("documents.manage", "Manage documents", "Create, edit, archive, restore, and request deletion of documents in the current workspace.", true, 20, ["admin", "member"])
        ],
        AssistantTools =
        [
            new("trykatch_list_documents", "Documents_List", "List documents in the current workspace.", TrykatchAssistantToolRisk.ReadOnly, false),
            new("trykatch_create_document", "Documents_Create", "Create a document record in the current workspace.", TrykatchAssistantToolRisk.Mutating, true),
            new("trykatch_update_document", "Documents_Update", "Update document content in the current workspace.", TrykatchAssistantToolRisk.Mutating, true)
        ]
    };

    public IReadOnlyList<TrykatchModuleMigration> Migrations { get; } =
    [
        new("202609101300_initial", """
            CREATE TABLE app.documents
            (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "Title" character varying(200) NOT NULL,
                "Content" text NOT NULL,
                "CreatedBy" uuid NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ArchivedAt" timestamp with time zone NULL
            );
            CREATE INDEX "IX_documents_OrganizationId_CreatedAt"
                ON app.documents ("OrganizationId", "CreatedAt" DESC);
            ALTER TABLE app.documents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.documents FORCE ROW LEVEL SECURITY;
            CREATE POLICY documents_organization_isolation ON app.documents
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            """),
        new("202609102100_recoverable_lifecycle", """
            ALTER TABLE app.documents
              ADD COLUMN "UpdatedAt" timestamp with time zone NULL,
              ADD COLUMN "ArchivedBy" uuid NULL,
              ADD COLUMN "DeletedAt" timestamp with time zone NULL,
              ADD COLUMN "DeletedBy" uuid NULL,
              ADD COLUMN "DeletionReason" character varying(500) NULL;
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IApplicationModelContributor, DocumentsModelContributor>();
        services.AddSingleton<ITrykatchOrganizationEndpointContributor>(this);
        services.AddScoped<DocumentsUseCases>();
    }

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        RouteGroupBuilder group = organizationApi.MapGroup("/documents");
        group.MapGet("/", ListAsync).RequireAuthorization("permission:documents.read")
            .WithName("Documents_List").WithTags("Documents").Produces<DocumentDto[]>();
        group.MapPost("/", CreateAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Documents_Create").WithTags("Documents")
            .Produces<DocumentDto>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Documents_Update").WithTags("Documents")
            .Produces<DocumentDto>().ProducesValidationProblem();
        group.MapPost("/{id:guid}/archive", ArchiveAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Documents_Archive").WithTags("Documents")
            .Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/restore", RestoreAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Documents_Restore").WithTags("Documents")
            .Produces(StatusCodes.Status204NoContent);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Documents_Delete").WithTags("Documents")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
    }

    private static async Task<IResult> ListAsync(DocumentsUseCases useCases, CancellationToken cancellationToken, string lifecycle = "active") =>
        ToResult(await useCases.ListAsync(lifecycle, cancellationToken));

    private static async Task<IResult> CreateAsync(SaveDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken)
    {
        DocumentOperationResult<DocumentDto> result = await useCases.CreateAsync(request, cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? Results.Created($"/api/v1/documents/{result.Value.Id}", result.Value)
            : ToResult(result);
    }

    private static async Task<IResult> UpdateAsync(Guid id, SaveDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.UpdateAsync(id, request, cancellationToken));
    private static async Task<IResult> ArchiveAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.ArchiveAsync(id, cancellationToken));
    private static async Task<IResult> RestoreAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.RestoreAsync(id, cancellationToken));
    private static async Task<IResult> DeleteAsync(Guid id, [FromBody] DeleteDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.DeleteAsync(id, request, cancellationToken));

    private static IResult ToResult<T>(DocumentOperationResult<T> result)
    {
        if (result.IsSuccess) return result.Value is null || result.Value is bool ? Results.NoContent() : Results.Ok(result.Value);
        return result.Code switch
        {
            "forbidden" => Results.Forbid(),
            "not_found" => Results.NotFound(),
            "conflict" => Results.Conflict(new { detail = result.Error }),
            "validation" => Results.ValidationProblem(new Dictionary<string, string[]> { ["document"] = [result.Error!] }),
            _ => Results.Problem(result.Error)
        };
    }
}

/// <summary>Module-owned application boundary; HTTP handlers only translate typed results.</summary>
public sealed class DocumentsUseCases(IOrganizationModuleData data, IModulePermissionAuthorizer authorizer, TimeProvider timeProvider)
{
    public async Task<DocumentOperationResult<DocumentDto[]>> ListAsync(string lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("documents.read", cancellationToken))
            return DocumentOperation.Failure<DocumentDto[]>("forbidden", "Documents cannot be viewed by this membership.");
        IQueryable<DocumentRecord> query = data.Query<DocumentRecord>();
        if (string.Equals(lifecycle, "recoverable", StringComparison.OrdinalIgnoreCase))
            query = query.IgnoreQueryFilters(["LifecycleVisibility"])
                .Where(document => document.ArchivedAt != null || document.DeletedAt != null);
        else if (!string.Equals(lifecycle, "active", StringComparison.OrdinalIgnoreCase))
            return DocumentOperation.Failure<DocumentDto[]>("validation", "Lifecycle must be active or recoverable.");
        DocumentRecord[] records = await query.AsNoTracking().OrderByDescending(document => document.CreatedAt).ToArrayAsync(cancellationToken);
        return DocumentOperation.Success(records.Select(ToDto).ToArray());
    }

    public async Task<DocumentOperationResult<DocumentDto>> CreateAsync(SaveDocumentRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = Validate(request);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);
        DocumentRecord document = DocumentRecord.Create(data.OrganizationId, data.ActorId, request.Title, request.Content, timeProvider.GetUtcNow());
        data.Add(document);
        RecordChange(document, "created");
        await data.SaveChangesAsync(cancellationToken);
        return DocumentOperation.Success(ToDto(document));
    }

    public async Task<DocumentOperationResult<DocumentDto>> UpdateAsync(Guid id, SaveDocumentRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<DocumentDto>();
        string? error = Validate(request);
        if (error is not null) return DocumentOperation.Failure<DocumentDto>("validation", error);
        DocumentRecord? document = await data.Query<DocumentRecord>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (document is null) return NotFound<DocumentDto>();
        document.Update(request.Title, request.Content, timeProvider.GetUtcNow());
        RecordChange(document, "updated");
        await data.SaveChangesAsync(cancellationToken);
        return DocumentOperation.Success(ToDto(document));
    }

    public async Task<DocumentOperationResult<bool>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        DocumentRecord? document = await data.Query<DocumentRecord>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.Archive(data.ActorId, timeProvider.GetUtcNow()))
        {
            RecordChange(document, "archived");
            await data.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    public async Task<DocumentOperationResult<bool>> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        DocumentRecord? document = await data.Query<DocumentRecord>().IgnoreQueryFilters(["LifecycleVisibility"])
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.Restore())
        {
            RecordChange(document, "restored");
            await data.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    public async Task<DocumentOperationResult<bool>> DeleteAsync(Guid id, DeleteDocumentRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        string reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
            return DocumentOperation.Failure<bool>("validation", "A deletion reason containing 10-500 characters is required.");
        DocumentRecord? document = await data.Query<DocumentRecord>().IgnoreQueryFilters(["LifecycleVisibility"])
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (document is null) return NotFound<bool>();
        if (document.LifecycleState != DocumentLifecycleState.Archived)
            return DocumentOperation.Failure<bool>("conflict", "Archive the document before requesting deletion.");
        if (document.RequestDeletion(data.ActorId, reason, timeProvider.GetUtcNow()))
        {
            RecordChange(document, "deleted");
            await data.SaveChangesAsync(cancellationToken);
        }
        return DocumentOperation.Success(true);
    }

    private Task<bool> CanManage(CancellationToken cancellationToken) => authorizer.HasPermissionAsync("documents.manage", cancellationToken);
    private void RecordChange(DocumentRecord document, string operation)
    {
        IReadOnlyDictionary<string, string?>? details = operation == "deleted"
            ? new Dictionary<string, string?> { ["reason"] = document.DeletionReason }
            : null;
        data.RecordAudit($"document.{operation}", "Document", document.Id.ToString(), document.Title, details);
        data.Enqueue(new DocumentChanged(document.Id, document.OrganizationId, operation, data.ActorId,
            timeProvider.GetUtcNow(), document.DeletionReason));
    }
    private static string? Validate(SaveDocumentRequest request) =>
        string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200
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
        ? DocumentLifecycleState.Deleted : ArchivedAt is not null ? DocumentLifecycleState.Archived : DocumentLifecycleState.Active;

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

public sealed class DocumentsModelContributor : IApplicationModelContributor
{
    public string ModuleId => "documents";
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRecord>(entity =>
        {
            entity.ToTable("documents", "app", table => table.ExcludeFromMigrations());
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Title).HasMaxLength(200);
            entity.Property(document => document.DeletionReason).HasMaxLength(500);
            entity.Ignore(document => document.LifecycleState);
            entity.HasIndex(document => new { document.OrganizationId, document.CreatedAt });
            entity.HasQueryFilter("LifecycleVisibility", document => document.ArchivedAt == null && document.DeletedAt == null);
        });
    }
}

public sealed record SaveDocumentRequest(string Title, string? Content);
public sealed record DeleteDocumentRequest(string? Reason);
public sealed record DocumentMetadataDto(DateTimeOffset? UpdatedAt, string MediaType, int CharacterCount);
public sealed record DocumentLifecycleDto(string Status, DateTimeOffset? ArchivedAt, Guid? ArchivedBy,
    DateTimeOffset? DeletedAt, Guid? DeletedBy, string? DeletionReason);
public sealed record DocumentDto(Guid Id, string Title, string Content, DateTimeOffset CreatedAt,
    DocumentMetadataDto Metadata, DocumentLifecycleDto Lifecycle);
public sealed record DocumentChanged(Guid DocumentId, Guid OrganizationId, string Operation, Guid ActorId,
    DateTimeOffset OccurredAt, string? Reason = null);

public sealed record DocumentOperationResult<T>(bool IsSuccess, T? Value, string? Code, string? Error);

public static class DocumentOperation
{
    public static DocumentOperationResult<T> Success<T>(T value) => new(true, value, null, null);
    public static DocumentOperationResult<T> Failure<T>(string code, string error) => new(false, default, code, error);
}
