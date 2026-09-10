using TrykatchApp.Domain.Organizations;

namespace TrykatchApp.Application.Organizations;

public sealed record OrganizationDataPlacementRequest(
    Guid OrganizationId,
    OrganizationDataPlacementKind Placement,
    string? Provider = null,
    string? RegionOrStamp = null);

public sealed record OrganizationDataRoute(
    Guid OrganizationId,
    OrganizationDataPlacementKind Placement,
    string Provider,
    string DatabaseIdentifier,
    string SecretReference,
    string SchemaVersion);

public sealed record OrganizationDataPlacementResult(
    OrganizationProvisioningState State,
    OrganizationDataRoute? Route = null,
    string? FailureCode = null)
{
    public bool IsReady => State == OrganizationProvisioningState.Ready && Route is not null;
}

/// <summary>
/// Deep host-owned seam for application data placement. Callers never construct
/// connections, handle credentials, or run migrations themselves.
/// </summary>
public interface IOrganizationDataPlacement
{
    Task<OrganizationDataPlacementResult> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken);

    Task<OrganizationDataRoute> ResolveAsync(
        Guid organizationId,
        CancellationToken cancellationToken);
}
