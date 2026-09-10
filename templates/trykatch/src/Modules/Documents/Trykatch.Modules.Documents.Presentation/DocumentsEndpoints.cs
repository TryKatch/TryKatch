using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Documents.Application;

namespace Trykatch.Modules.Documents.Presentation;

public sealed record SaveDocumentRequest(string Title, string? Content);
public sealed record DeleteDocumentRequest(string? Reason);

public sealed class DocumentsEndpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "documents";

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
        DocumentOperationResult<DocumentDto> result = await useCases.CreateAsync(new(request.Title, request.Content), cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? Results.Created($"/api/v1/documents/{result.Value.Id}", result.Value)
            : ToResult(result);
    }

    private static async Task<IResult> UpdateAsync(Guid id, SaveDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.UpdateAsync(id, new(request.Title, request.Content), cancellationToken));
    private static async Task<IResult> ArchiveAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.ArchiveAsync(id, cancellationToken));
    private static async Task<IResult> RestoreAsync(Guid id, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.RestoreAsync(id, cancellationToken));
    private static async Task<IResult> DeleteAsync(Guid id, [FromBody] DeleteDocumentRequest request, DocumentsUseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.DeleteAsync(id, request.Reason, cancellationToken));

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
