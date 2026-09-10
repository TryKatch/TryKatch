using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Federation.Application;
using Trykatch.Modules.Federation.Domain;

namespace Trykatch.Modules.Federation.Presentation;

public sealed class FederationEndpoints : IPlatformEndpointContributor
{
    private const string ReadPermission = "platform.authentication.read";
    private const string ManagePermission = "platform.authentication.manage";

    public string ModuleId => "federation";
    public string RequiredPlatformPermission => ReadPermission;

    public void MapEndpoints(RouteGroupBuilder platformApi)
    {
        RouteGroupBuilder group = platformApi.MapGroup("/federation/connections");
        group.MapGet("/", async (IFederationConnectionStore store, CancellationToken cancellationToken) =>
                Results.Ok(await store.ListAsync(cancellationToken)))
            .WithName("Federation_ListConnections").WithTags("Federation");
        group.MapPost("/", CreateAsync).RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_CreateConnection").WithTags("Federation");
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_UpdateConnection").WithTags("Federation");
        group.MapPost("/{id:guid}/test", TestAsync).RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_TestConnection").WithTags("Federation");
        group.MapPost("/{id:guid}/enable", (Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
                ChangeStatusAsync(id, true, service, cancellationToken))
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_EnableConnection").WithTags("Federation");
        group.MapPost("/{id:guid}/disable", (Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
                ChangeStatusAsync(id, false, service, cancellationToken))
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_DisableConnection").WithTags("Federation");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Federation_DeleteConnection").WithTags("Federation");
    }

    private static async Task<IResult> CreateAsync(SaveFederationConnectionRequest request, FederationConnectionService service, CancellationToken cancellationToken)
    {
        FederationOperationResult<FederationConnection> result = await service.CreateAsync(request, cancellationToken);
        return ToResult(result, value => Results.Created($"/api/v1/platform/federation/connections/{value.Id}", value));
    }

    private static async Task<IResult> UpdateAsync(Guid id, SaveFederationConnectionRequest request, FederationConnectionService service, CancellationToken cancellationToken) =>
        ToResult(await service.UpdateAsync(id, request, cancellationToken), Results.Ok);
    private static async Task<IResult> TestAsync(Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
        ToResult(await service.TestAsync(id, cancellationToken), Results.Ok);
    private static async Task<IResult> ChangeStatusAsync(Guid id, bool enabled, FederationConnectionService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(id, enabled, cancellationToken), Results.Ok);
    private static async Task<IResult> DeleteAsync(Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
        ToResult(await service.DeleteAsync(id, cancellationToken), _ => Results.NoContent());

    private static IResult ToResult<T>(FederationOperationResult<T> result, Func<T, IResult> success) =>
        result.Value is not null
            ? success(result.Value)
            : Results.Problem(
                statusCode: result.Code switch
                {
                    "not_found" => StatusCodes.Status404NotFound,
                    "conflict" => StatusCodes.Status409Conflict,
                    "provider_unavailable" => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status400BadRequest
                },
                title: result.Code,
                detail: result.Error);
}
