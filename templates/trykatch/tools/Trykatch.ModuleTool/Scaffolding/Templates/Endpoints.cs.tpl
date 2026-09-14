using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using __ROOT_NAMESPACE__.Modules.AspNetCore;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Presentation;

public sealed record Save__ENTITY__Request(
    __COMMAND_FIELDS__);
public sealed record Delete__ENTITY__Request(string? Reason);

public sealed class __MODULE__Endpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "__MODULE_ID__";

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        RouteGroupBuilder group = organizationApi.MapGroup("/__RESOURCE__");
        group.MapGet("/", ListAsync).RequireAuthorization("permission:__MODULE_ID__.read")
            .WithName("__MODULE___List").WithTags("__MODULE__").Produces<__ENTITY__Dto[]>();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization("permission:__MODULE_ID__.read")
            .WithName("__MODULE___Get").WithTags("__MODULE__").Produces<__ENTITY__Dto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/", CreateAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Create").WithTags("__MODULE__")
            .Produces<__ENTITY__Dto>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Update").WithTags("__MODULE__")
            .Produces<__ENTITY__Dto>().ProducesValidationProblem();
        group.MapPost("/{id:guid}/archive", ArchiveAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Archive").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/restore", RestoreAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Restore").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent);
        group.MapDelete("/{id:guid}", RequestDeletionAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___RequestDeletion").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
    }

    private static async Task<IResult> ListAsync(__MODULE__UseCases useCases, CancellationToken cancellationToken, string lifecycle = "active")
    {
        try { return ToResult(await useCases.ListAsync(lifecycle, cancellationToken)); }
        catch (ArgumentException exception) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["lifecycle"] = [exception.Message] }); }
    }

    private static async Task<IResult> GetAsync(Guid id, __MODULE__UseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.GetAsync(id, cancellationToken));

    private static async Task<IResult> CreateAsync(Save__ENTITY__Request request, __MODULE__UseCases useCases, CancellationToken cancellationToken)
    {
        __ENTITY__OperationResult<__ENTITY__Dto> result = await useCases.CreateAsync(
            new(__REQUEST_ARGUMENTS__), cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? TypedResults.Created($"/api/v1/__RESOURCE__/{result.Value.Id}", result.Value)
            : ToResult(result);
    }

    private static async Task<IResult> UpdateAsync(Guid id, Save__ENTITY__Request request, __MODULE__UseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.UpdateAsync(id, new(__REQUEST_ARGUMENTS__), cancellationToken));
    private static async Task<IResult> ArchiveAsync(Guid id, __MODULE__UseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.ArchiveAsync(id, cancellationToken));
    private static async Task<IResult> RestoreAsync(Guid id, __MODULE__UseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.RestoreAsync(id, cancellationToken));
    private static async Task<IResult> RequestDeletionAsync(Guid id, [FromBody] Delete__ENTITY__Request request, __MODULE__UseCases useCases, CancellationToken cancellationToken) =>
        ToResult(await useCases.RequestDeletionAsync(id, request.Reason, cancellationToken));

    private static IResult ToResult<T>(__ENTITY__OperationResult<T> result)
    {
        if (result.IsSuccess) return result.Value is null || result.Value is bool ? TypedResults.NoContent() : TypedResults.Ok(result.Value);
        return result.Code switch
        {
            "forbidden" => TypedResults.Forbid(),
            "not_found" => TypedResults.NotFound(),
            "conflict" => TypedResults.Conflict(new { detail = result.Error }),
            "validation" => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["__ENTITY_CAMEL__"] = [result.Error!] }),
            _ => TypedResults.Problem(result.Error)
        };
    }
}
