using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Documents.Application;

namespace Trykatch.Modules.Documents.Presentation;

public sealed record UpdateDocumentRequest(string Title, string? Description, string? DocumentType = null);
public sealed record DeleteDocumentRequest(string? Reason);
public sealed record UploadDocumentForm(string Title, string? Description, IFormFile File, string? DocumentType = null);

public sealed class DocumentsEndpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "documents";

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        RouteGroupBuilder group = organizationApi.MapGroup("/documents");
        group.MapGet("/", ListAsync).RequireAuthorization("permission:documents.read")
            .WithName("Documents_List").WithTags("Documents").Produces<DocumentDto[]>();
        group.MapPost("/", UploadAsync).RequireAuthorization("permission:documents.manage")
            .WithMetadata(
                new RequireAntiforgeryTokenAttribute(true),
                new RequestSizeLimitAttribute(DocumentUploadPolicy.MaximumRequestBytes))
            .Accepts<UploadDocumentForm>("multipart/form-data")
            .WithName("Documents_Upload").WithTags("Documents")
            .Produces<DocumentDto>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapGet("/{id:guid}/content", DownloadAsync).RequireAuthorization("permission:documents.read")
            .WithName("Documents_Download").WithTags("Documents")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound);
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

    private static async Task<IResult> ListAsync(
        DocumentsUseCases useCases,
        CancellationToken cancellationToken,
        string lifecycle = "active") =>
        ToResult(await useCases.ListAsync(lifecycle, cancellationToken));

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        DocumentsUseCases useCases,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Validation("file", "Use multipart/form-data to upload a document.");

        IFormCollection form = await request.ReadFormAsync(cancellationToken);
        IFormFile? file = form.Files.GetFile("file");
        if (file is null)
            return Validation("file", "Choose a file to upload.");

        string mediaType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType.Trim();
        string checksum;
        await using (Stream checksumStream = file.OpenReadStream())
        {
            checksum = Convert.ToHexString(await SHA256.HashDataAsync(checksumStream, cancellationToken));
        }

        await using Stream uploadStream = file.OpenReadStream();
        DocumentOperationResult<DocumentDto> result = await useCases.UploadAsync(
            new(
                form["title"].ToString(),
                form["description"].ToString(),
                file.FileName,
                mediaType,
                file.Length,
                checksum,
                uploadStream,
                form.ContainsKey("documentType") ? form["documentType"].ToString() : null),
            cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? Results.Created($"/api/v1/documents/{result.Value.Id}", result.Value)
            : ToResult(result);
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        DocumentsUseCases useCases,
        CancellationToken cancellationToken)
    {
        DocumentOperationResult<DocumentDownload> result = await useCases.DownloadAsync(id, cancellationToken);
        if (!result.IsSuccess || result.Value is null) return ToResult(result);
        DocumentDownload download = result.Value;
        return Results.Stream(
            download.Content,
            download.MediaType,
            download.FileName,
            enableRangeProcessing: false);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        DocumentsUseCases useCases,
        CancellationToken cancellationToken) =>
        ToResult(await useCases.UpdateAsync(id, new(request.Title, request.Description, request.DocumentType), cancellationToken));
    private static async Task<IResult> ArchiveAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.ArchiveAsync(id, cancellationToken));
    private static async Task<IResult> RestoreAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.RestoreAsync(id, cancellationToken));
    private static async Task<IResult> DeleteAsync(Guid id, [FromBody] DeleteDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.DeleteAsync(id, request.Reason, cancellationToken));

    private static IResult Validation(string field, string error) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });

    private static IResult ToResult<T>(DocumentOperationResult<T> result)
    {
        if (result.IsSuccess) return result.Value is null || result.Value is bool ? Results.NoContent() : Results.Ok(result.Value);
        return result.Code switch
        {
            "forbidden" => Results.Forbid(),
            "not_found" => Results.NotFound(),
            "conflict" => Results.Conflict(new { detail = result.Error }),
            "validation" => Validation("document", result.Error!),
            _ => Results.Problem(result.Error)
        };
    }
}
