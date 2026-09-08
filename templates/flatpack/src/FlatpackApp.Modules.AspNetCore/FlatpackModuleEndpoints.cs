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

/// <summary>
/// The narrow HTTP seam for a module. The host supplies an authenticated,
/// organization-scoped route group; contributors can only add routes beneath it.
/// </summary>
public interface IFlatpackOrganizationEndpointContributor
{
    void MapEndpoints(RouteGroupBuilder organizationApi);
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
        foreach (IFlatpackOrganizationEndpointContributor contributor in contributors)
            contributor.MapEndpoints(organizationApi);

        return endpoints;
    }
}
