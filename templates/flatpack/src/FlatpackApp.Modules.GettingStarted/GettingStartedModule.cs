using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Modules.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Modules.GettingStarted;

public static class GettingStartedPermissions
{
    public const string Read = "getting-started.read";
}

/// <summary>
/// Reference installable module. It owns its metadata, registration, permission
/// vocabulary, API contribution, and implementation behind one small interface.
/// </summary>
public sealed class GettingStartedModule : IFlatpackModule, IFlatpackOrganizationEndpointContributor
{
    public string ModuleId => Descriptor.Id;

    public FlatpackModuleDescriptor Descriptor { get; } = new(
        Id: "getting-started",
        Name: "Getting Started",
        Version: "1.0.0",
        Description: "A reference module that proves Flatpack's backend and frontend composition seams.",
        Requires: ["projects"],
        OptionalDependencies: [],
        Capabilities: FlatpackModuleCapabilities.Api
            | FlatpackModuleCapabilities.Web
            | FlatpackModuleCapabilities.Assistant,
        ExtensionPoints: [])
    {
        AssistantTools =
        [
            new(
                "flatpack_get_module_readiness",
                "GettingStarted_Get",
                "Check whether the current organization request crossed the identity, organization, permission, and frontend module seams.",
                FlatpackAssistantToolRisk.ReadOnly,
                RequiresHumanConfirmation: false)
        ]
    };

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IFlatpackOrganizationEndpointContributor>(this);
        services.AddSingleton<IGettingStartedExperience, GettingStartedExperience>();
        services.AddSingleton<IPermissionDefinitionProvider, GettingStartedPermissionDefinitionProvider>();
    }

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        organizationApi.MapGet("/getting-started", GetAsync)
            .RequireAuthorization($"permission:{GettingStartedPermissions.Read}")
            .WithName("GettingStarted_Get")
            .WithTags("Getting Started");
    }

    private static IResult GetAsync(
        [FromServices] IGettingStartedExperience experience,
        [FromServices] IOrganizationContext organization) =>
        Results.Ok(experience.Describe(organization));
}

public sealed record GettingStartedStep(string Id, string Title, string Description, bool Complete);
public sealed record GettingStartedResponse(string ModuleId, string Organization, IReadOnlyList<GettingStartedStep> Steps);

internal interface IGettingStartedExperience
{
    GettingStartedResponse Describe(IOrganizationContext organization);
}

internal sealed class GettingStartedExperience : IGettingStartedExperience
{
    public GettingStartedResponse Describe(IOrganizationContext organization) => new(
        "getting-started",
        organization.OrganizationSlug,
        [
            new("identity", "Identity and session", "The request carries an authenticated actor.", organization.ActorId != Guid.Empty),
            new("organization", "Organization resolution", "The workspace was inferred before this module executed.", organization.IsResolved),
            new("permission", "Permission enforcement", "The module-owned permission was authorized by the Flatpack kernel.", organization.Permissions.Contains(GettingStartedPermissions.Read)),
            new("extension", "Full-stack extension", "This response powers both a route and a Projects extension slot.", true)
        ]);
}

internal sealed class GettingStartedPermissionDefinitionProvider : IPermissionDefinitionProvider
{
    private static readonly IReadOnlyList<PermissionModuleDefinition> Modules =
    [
        new("getting-started", "Getting started", "Reference-module access and verification.", 90,
        [
            new(
                GettingStartedPermissions.Read,
                "View getting started",
                "View the reference module and its integration status.",
                Order: 10,
                DefaultRoles:
                [
                    DefaultOrganizationRoles.Admin,
                    DefaultOrganizationRoles.Member,
                    DefaultOrganizationRoles.Viewer
                ])
        ])
    ];

    public IReadOnlyList<PermissionModuleDefinition> GetModules() => Modules;
}
