using Microsoft.AspNetCore.Antiforgery;
using System.Text.Json.Serialization;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using __ROOT_NAMESPACE__.Modules.AspNetCore;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Presentation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Save__ENTITY__Request(
    __COMMAND_FIELDS__);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Delete__ENTITY__Request(string? Reason, Guid ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Version__ENTITY__Request(Guid ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Update__ENTITY__Request(__COMMAND_FIELDS__, Guid ExpectedVersion);

public sealed partial class __MODULE__Endpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "__MODULE_ID__";

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        RouteGroupBuilder group = organizationApi.MapGroup("/__RESOURCE__");
        group.AddEndpointFilter<BlueprintFailureFilter>();
        MapActions(group);
        group.MapGet("/", ListAsync).RequireAuthorization("permission:__MODULE_ID__.read")
            .WithName("__MODULE___List").WithTags("__MODULE__").Produces<__ENTITY__Dto[]>();
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization("permission:__MODULE_ID__.read")
            .WithName("__MODULE___Get").WithTags("__MODULE__").Produces<__ENTITY__Dto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/", CreateAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Create").WithTags("__MODULE__")
            .Produces<__ENTITY__Dto>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Update").WithTags("__MODULE__")
            .Produces<__ENTITY__Dto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/archive", ArchiveAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Archive").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/restore", RestoreAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___Restore").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{id:guid}", RequestDeletionAsync).RequireAuthorization("permission:__MODULE_ID__.manage")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("__MODULE___RequestDeletion").WithTags("__MODULE__")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Ok<__ENTITY__Dto[]>, ForbidHttpResult, ValidationProblem>> ListAsync(
        List__ENTITY__QueryHandler handler, CancellationToken cancellationToken, string lifecycle = "active")
    {
        try
        {
            __ENTITY__OperationResult<__ENTITY__Dto[]> result = await handler.HandleAsync(lifecycle, cancellationToken);
            return result.IsSuccess
                ? TypedResults.Ok(result.Value!)
                : TypedResults.Forbid();
        }
        catch (ArgumentException exception) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["lifecycle"] = [exception.Message] }); }
    }

    private static async Task<Results<Ok<__ENTITY__Dto>, ForbidHttpResult, NotFound>> GetAsync(
        Guid id, Get__ENTITY__QueryHandler handler, CancellationToken cancellationToken)
    {
        __ENTITY__OperationResult<__ENTITY__Dto> result = await handler.HandleAsync(id, cancellationToken);
        if (result.IsSuccess) return TypedResults.Ok(result.Value!);
        return result.Code == "forbidden" ? TypedResults.Forbid() : TypedResults.NotFound();
    }

    private static async Task<Results<Created<__ENTITY__Dto>, ForbidHttpResult, ValidationProblem>> CreateAsync(
        Save__ENTITY__Request request, Create__ENTITY__CommandHandler handler, CancellationToken cancellationToken)
    {
        __ENTITY__OperationResult<__ENTITY__Dto> result = await handler.HandleAsync(
            new(__REQUEST_ARGUMENTS__), cancellationToken);
        if (result.IsSuccess)
            return TypedResults.Created($"/api/v1/__RESOURCE__/{result.Value!.Id}", result.Value);
        return result.Code == "forbidden"
            ? TypedResults.Forbid()
            : ValidationFailure(result.Error);
    }

    private static async Task<Results<Ok<__ENTITY__Dto>, ForbidHttpResult, NotFound, ValidationProblem>> UpdateAsync(
        Guid id, Update__ENTITY__Request request, Update__ENTITY__CommandHandler handler, CancellationToken cancellationToken)
    {
        __ENTITY__OperationResult<__ENTITY__Dto> result = await handler.HandleAsync(
            id, request.ExpectedVersion, new(__REQUEST_ARGUMENTS__), cancellationToken);
        if (result.IsSuccess) return TypedResults.Ok(result.Value!);
        return result.Code switch
        {
            "forbidden" => TypedResults.Forbid(),
            "not_found" => TypedResults.NotFound(),
            _ => ValidationFailure(result.Error)
        };
    }

    private static Task<Results<NoContent, ForbidHttpResult, NotFound>> ArchiveAsync(
        Guid id, Version__ENTITY__Request request, __ENTITY__RecoveryCommands handler, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(handler.ArchiveAsync(id, request.ExpectedVersion, cancellationToken));

    private static Task<Results<NoContent, ForbidHttpResult, NotFound>> RestoreAsync(
        Guid id, Version__ENTITY__Request request, __ENTITY__RecoveryCommands handler, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(handler.RestoreAsync(id, request.ExpectedVersion, cancellationToken));

    private static async Task<Results<NoContent, ForbidHttpResult, NotFound, Conflict<ProblemDetails>, ValidationProblem>> RequestDeletionAsync(
        Guid id, [FromBody] Delete__ENTITY__Request request, __ENTITY__RecoveryCommands handler, CancellationToken cancellationToken)
    {
        __ENTITY__OperationResult<bool> result = await handler.RequestDeletionAsync(id, request.ExpectedVersion, request.Reason, cancellationToken);
        if (result.IsSuccess) return TypedResults.NoContent();
        return result.Code switch
        {
            "forbidden" => TypedResults.Forbid(),
            "not_found" => TypedResults.NotFound(),
            "conflict" => TypedResults.Conflict(new ProblemDetails { Detail = result.Error }),
            _ => ValidationFailure(result.Error)
        };
    }

    private static async Task<Results<NoContent, ForbidHttpResult, NotFound>> ChangeLifecycleAsync(
        Task<__ENTITY__OperationResult<bool>> operation)
    {
        __ENTITY__OperationResult<bool> result = await operation;
        if (result.IsSuccess) return TypedResults.NoContent();
        return result.Code == "forbidden" ? TypedResults.Forbid() : TypedResults.NotFound();
    }

    private static ValidationProblem ValidationFailure(string? error) => TypedResults.ValidationProblem(
        new Dictionary<string, string[]> { ["__ENTITY_CAMEL__"] = [error ?? "The request is invalid."] });
}
