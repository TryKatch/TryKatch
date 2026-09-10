namespace Trykatch.Application.Organizations;

public interface IOrganizationContext
{
    bool IsResolved { get; }
    Guid OrganizationId { get; }
    string OrganizationSlug { get; }
    Guid ActorId { get; }
    Guid MembershipId { get; }
    IReadOnlySet<string> Permissions { get; }
}

public sealed record OrganizationAccess(
    Guid OrganizationId,
    string OrganizationSlug,
    Guid ActorId,
    Guid MembershipId,
    IReadOnlySet<string> Permissions);

public interface IOrganizationAccessResolver
{
    Task<OrganizationAccess?> ResolveAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken = default);
}
