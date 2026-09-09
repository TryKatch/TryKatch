using TrykatchApp.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TrykatchApp.Modules.AspNetCore;

/// <summary>
/// Marker applied by the host to every organization-scoped module endpoint.
/// Organization resolution and transaction middleware use this metadata before
/// module code can execute.
/// </summary>
public interface ITrykatchOrganizationScopedMetadata;

public sealed class TrykatchOrganizationScopedMetadata : ITrykatchOrganizationScopedMetadata;

/// <summary>Identifies the module that owns a contributed endpoint.</summary>
public sealed record TrykatchModuleEndpointMetadata(string ModuleId);

/// <summary>
/// The narrow HTTP seam for a module. The host supplies an authenticated,
/// organization-scoped route group; contributors can only add routes beneath it.
/// </summary>
public interface ITrykatchOrganizationEndpointContributor
{
    string ModuleId { get; }
    void MapEndpoints(RouteGroupBuilder organizationApi);
}

/// <summary>
/// Narrow platform-administration seam. The host owns authentication and the
/// platform permission boundary; modules can only add routes beneath it.
/// </summary>
public interface ITrykatchPlatformEndpointContributor
{
    string ModuleId { get; }
    string RequiredPlatformPermission { get; }
    void MapEndpoints(RouteGroupBuilder platformApi);
}

public static class TrykatchModuleEndpointExtensions
{
    /// <summary>
    /// Maps endpoints contributed by explicitly enabled modules. This method does
    /// not scan assemblies and the host retains ownership of the security boundary.
    /// </summary>
    public static IEndpointRouteBuilder MapTrykatchOrganizationModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder organizationApi = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization()
            .WithMetadata(new TrykatchOrganizationScopedMetadata());

        IEnumerable<ITrykatchOrganizationEndpointContributor> contributors = endpoints
            .ServiceProvider
            .GetServices<ITrykatchOrganizationEndpointContributor>();
        TrykatchModuleCatalog catalog = endpoints.ServiceProvider.GetRequiredService<TrykatchModuleCatalog>();
        foreach (ITrykatchOrganizationEndpointContributor contributor in contributors)
        {
            if (!catalog.Contains(contributor.ModuleId))
                throw new InvalidOperationException($"Endpoint contributor belongs to disabled Trykatch module '{contributor.ModuleId}'.");

            RouteGroupBuilder moduleApi = organizationApi
                .MapGroup(string.Empty)
                .WithMetadata(new TrykatchModuleEndpointMetadata(contributor.ModuleId));
            contributor.MapEndpoints(moduleApi);
        }

        return endpoints;
    }

    public static IEndpointRouteBuilder MapTrykatchPlatformModuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        TrykatchModuleCatalog catalog = endpoints.ServiceProvider.GetRequiredService<TrykatchModuleCatalog>();
        foreach (ITrykatchPlatformEndpointContributor contributor in endpoints.ServiceProvider
                     .GetServices<ITrykatchPlatformEndpointContributor>())
        {
            if (!catalog.Contains(contributor.ModuleId))
                throw new InvalidOperationException($"Platform endpoint contributor belongs to disabled Trykatch module '{contributor.ModuleId}'.");
            if (string.IsNullOrWhiteSpace(contributor.RequiredPlatformPermission))
                throw new InvalidOperationException($"Platform endpoint contributor '{contributor.ModuleId}' requires a platform permission boundary.");

            RouteGroupBuilder moduleApi = endpoints
                .MapGroup("/api/v1/platform")
                .RequireAuthorization($"platform-permission:{contributor.RequiredPlatformPermission}")
                .WithMetadata(new TrykatchModuleEndpointMetadata(contributor.ModuleId));
            contributor.MapEndpoints(moduleApi);
        }
        return endpoints;
    }
}
