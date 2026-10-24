using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Trykatch.Application.Common;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Projects.Application;

namespace Trykatch.Modules.Projects.Presentation;

/// <summary>Translates the Projects application interface into organization-scoped HTTP endpoints.</summary>
public sealed class ProjectsEndpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "projects";

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        RouteGroupBuilder group = organizationApi.MapGroup("/projects");
        group.MapGet("/", ListAsync).RequireModuleFeature(ProjectFeatures.Reads).RequireAuthorization("permission:projects.read")
            .WithName("Projects_List").WithTags("Projects").Produces<PagedResult<ProjectDto>>();
        group.MapGet("/{id:guid}", GetAsync).RequireModuleFeature(ProjectFeatures.Reads).RequireAuthorization("permission:projects.read")
            .WithMetadata(new ModuleReadOnlyTransactionMetadata())
            .WithName("Projects_Get").WithTags("Projects").Produces<ProjectDto>();
        group.MapPost("/", CreateAsync).RequireModuleFeature(ProjectFeatures.Writes).RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default, AuthorizeMutationRequestAsync, AuthorizeMutationReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Projects_Create").WithTags("Projects")
            .Produces<ProjectDto>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapPut("/{id:guid}", UpdateAsync).RequireModuleFeature(ProjectFeatures.Writes).RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default, AuthorizeMutationRequestAsync, AuthorizeMutationReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Projects_Update").WithTags("Projects")
            .Produces<ProjectDto>().ProducesValidationProblem();
        group.MapPost("/{id:guid}/archive", ArchiveAsync).RequireModuleFeature(ProjectFeatures.Writes).RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default, AuthorizeMutationRequestAsync, AuthorizeMutationReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Projects_Archive").WithTags("Projects")
            .Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/restore", RestoreAsync).RequireModuleFeature(ProjectFeatures.Writes).RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default, AuthorizeMutationRequestAsync, AuthorizeMutationReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Projects_Restore").WithTags("Projects")
            .Produces(StatusCodes.Status204NoContent);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireModuleFeature(ProjectFeatures.Writes).RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default, AuthorizeMutationRequestAsync, AuthorizeMutationReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true)).WithName("Projects_Delete").WithTags("Projects")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
    }

    private static async Task<IResult> ListAsync(ProjectUseCases projects, CancellationToken cancellationToken,
        int page = 1, int pageSize = 25, string? search = null, string lifecycle = "active")
    {
        if (!RecordLifecycle.TryParseFilter(lifecycle, out RecordLifecycleFilter filter))
            return Problem("validation", "Lifecycle must be active, archived, deleted, recoverable, or all.");
        return ToResult(await projects.ListAsync(Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search, filter, cancellationToken));
    }

    private static async Task<IResult> GetAsync(Guid id, ProjectUseCases projects, CancellationToken cancellationToken) =>
        ToResult(await projects.GetAsync(id, cancellationToken));

    private static async Task<IResult> CreateAsync(CreateProjectCommand command, ProjectUseCases projects, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Result<ProjectDto> result = await projects.CreateAsync(command, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            return Problem(result.ErrorCode, result.ErrorMessage);

        ModuleMutationIdempotency.BindResource(httpContext, new MutationResourceReference("projects", result.Value.Id));
        return Results.Created($"/api/v1/projects/{result.Value.Id}", result.Value);
    }

    private static async Task<IResult> UpdateAsync(Guid id, CreateProjectCommand command, ProjectUseCases projects, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Result<ProjectDto> result = await projects.UpdateAsync(new UpdateProjectCommand(id, command.Name, command.Description), cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            return Problem(result.ErrorCode, result.ErrorMessage);

        ModuleMutationIdempotency.BindResource(httpContext, new MutationResourceReference("projects", id));
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ArchiveAsync(Guid id, ProjectUseCases projects, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.ArchiveAsync(id, cancellationToken);
        if (!result.IsSuccess)
            return Problem(result.ErrorCode, result.ErrorMessage);

        ModuleMutationIdempotency.BindResource(httpContext, new MutationResourceReference("projects", id));
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreAsync(Guid id, ProjectUseCases projects, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.RestoreAsync(id, cancellationToken);
        if (!result.IsSuccess)
            return Problem(result.ErrorCode, result.ErrorMessage);

        ModuleMutationIdempotency.BindResource(httpContext, new MutationResourceReference("projects", id));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, [FromBody] DeleteRecordCommand command, ProjectUseCases projects, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.DeleteAsync(id, command, cancellationToken);
        if (!result.IsSuccess)
            return Problem(result.ErrorCode, result.ErrorMessage);

        ModuleMutationIdempotency.BindResource(httpContext, new MutationResourceReference("projects", id));
        return Results.NoContent();
    }

    private static Task<bool> AuthorizeMutationRequestAsync(EndpointFilterInvocationContext invocation, MutationAuthority authority,
        CancellationToken cancellationToken)
    {
        Guid? projectId = null;
        if (invocation.HttpContext.Request.RouteValues.TryGetValue("id", out object? routeValue))
        {
            if (routeValue is Guid parsed)
                projectId = parsed;
            else if (Guid.TryParse(Convert.ToString(routeValue, CultureInfo.InvariantCulture), out parsed))
                projectId = parsed;
            else
                return Task.FromResult(false);
        }

        return invocation.HttpContext.RequestServices.GetRequiredService<ProjectMutationAuthorization>()
            .AuthorizeRequestAsync(authority, projectId, cancellationToken);
    }

    private static Task<bool> AuthorizeMutationReplayAsync(EndpointFilterInvocationContext invocation, MutationAuthority authority,
        MutationResourceReference? resource, CancellationToken cancellationToken) =>
        invocation.HttpContext.RequestServices.GetRequiredService<ProjectMutationAuthorization>()
            .AuthorizeReplayAsync(authority, resource, cancellationToken);

    private static IResult ToResult<T>(Result<T> result) =>
        result.IsSuccess && result.Value is not null ? Results.Ok(result.Value) : Problem(result.ErrorCode, result.ErrorMessage);

    private static IResult Problem(string? code, string? detail) => Results.Problem(
        statusCode: code switch { "forbidden" => 403, "not_found" => 404, "conflict" or "slug_conflict" or "feature_unavailable" => 409, _ => 400 },
        title: code,
        detail: detail);
}
