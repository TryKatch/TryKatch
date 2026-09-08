using FlatpackApp.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Modules.AspNetCore;

/// <summary>
/// Marker applied by the host to every organization-scoped module endpoint.
/// Organization resolution and transaction middleware use this metadata before
/// module code can execute.
/// </summary>
public interface IFlatpackOrganizationScopedMetadata;

public sealed class FlatpackOrganizationScopedMetadata : IFlatpackOrganizationScopedMetadata;

/// <summary>Identifies the module that owns a contributed endpoint.</summary>
public sealed record FlatpackModuleEndpointMetadata(string ModuleId);

/// <summary>
/// The narrow HTTP seam for a module. The host supplies an authenticated,
/// organization-scoped route group; contributors can only add routes beneath it.
/// </summary>
public interface IFlatpackOrganizationEndpointContributor
{
    string ModuleId { get; }
    void MapEndpoints(RouteGroupBuilder organizationApi);
}

/// <summary>
/// Narrow platform-administration seam. The host owns authentication and the
/// platform permission boundary; modules can only add routes beneath it.
/// </summary>
public interface IFlatpackPlatformEndpointContributor
{
    string ModuleId { get; }
    string RequiredPlatformPermission { get; }
    void MapEndpoints(RouteGroupBuilder platformApi);
}

public static class FlatpackModuleEndpointExtensions
{
    /// <summary>
    /// Maps endpoints contributed by explicitly enabled modules. This method does
    /// not scan assemblies and the host retains ownership of the security boundary.
    /// </summary>
    public static IEndpointRouteBuilder MapFlatpackOrganizationModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder organizationApi = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization()
            .WithMetadata(new FlatpackOrganizationScopedMetadata());

        IEnumerable<IFlatpackOrganizationEndpointContributor> contributors = endpoints
            .ServiceProvider
            .GetServices<IFlatpackOrganizationEndpointContributor>();
        FlatpackModuleCatalog catalog = endpoints.ServiceProvider.GetRequiredService<FlatpackModuleCatalog>();
        foreach (IFlatpackOrganizationEndpointContributor contributor in contributors)
        {
            if (!catalog.Contains(contributor.ModuleId))
                throw new InvalidOperationException($"Endpoint contributor belongs to disabled Flatpack module '{contributor.ModuleId}'.");

            RouteGroupBuilder moduleApi = organizationApi
                .MapGroup(string.Empty)
                .WithMetadata(new FlatpackModuleEndpointMetadata(contributor.ModuleId));
            contributor.MapEndpoints(moduleApi);
        }

        return endpoints;
    }

    public static IEndpointRouteBuilder MapFlatpackPlatformModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        FlatpackModuleCatalog catalog = endpoints.ServiceProvider.GetRequiredService<FlatpackModuleCatalog>();
        foreach (IFlatpackPlatformEndpointContributor contributor in endpoints.ServiceProvider
                     .GetServices<IFlatpackPlatformEndpointContributor>())
        {
            if (!catalog.Contains(contributor.ModuleId))
                throw new InvalidOperationException($"Platform endpoint contributor belongs to disabled Flatpack module '{contributor.ModuleId}'.");
            if (string.IsNullOrWhiteSpace(contributor.RequiredPlatformPermission))
                throw new InvalidOperationException($"Platform endpoint contributor '{contributor.ModuleId}' requires a platform permission boundary.");

            RouteGroupBuilder moduleApi = endpoints
                .MapGroup("/api/v1/platform")
                .RequireAuthorization($"platform-permission:{contributor.RequiredPlatformPermission}")
                .WithMetadata(new FlatpackModuleEndpointMetadata(contributor.ModuleId));
            contributor.MapEndpoints(moduleApi);
        }
        return endpoints;
    }
}
