using Trykatch.Application.Authorization;
using Trykatch.Modules;

namespace Trykatch.Modules.Projects.Application;

/// <summary>Authorizes idempotent mutation requests against the current Projects organization.</summary>
public sealed class ProjectMutationAuthorization(IProjectStore projects)
{
    public Task<bool> AuthorizeRequestAsync(MutationAuthority authority, Guid? projectId, CancellationToken cancellationToken)
    {
        if (!HasManagePermission(authority))
            return Task.FromResult(false);

        if (projectId is null)
            return Task.FromResult(true);

        if (projectId == Guid.Empty)
            return Task.FromResult(false);

        return ProjectExistsAsync(authority.OrganizationId, projectId.Value, cancellationToken);
    }

    public Task<bool> AuthorizeReplayAsync(MutationAuthority authority, MutationResourceReference? resource, CancellationToken cancellationToken)
    {
        if (!HasManagePermission(authority)
            || resource is null
            || !string.Equals(resource.ResourceKey, "projects", StringComparison.Ordinal)
            || resource.ResourceId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        return ProjectExistsAsync(authority.OrganizationId, resource.ResourceId, cancellationToken);
    }

    private static bool HasManagePermission(MutationAuthority? authority) =>
        authority is not null
        && authority.OrganizationId != Guid.Empty
        && authority.ActorId != Guid.Empty
        && authority.MembershipId != Guid.Empty
        && authority.Permissions is not null
        && authority.Permissions.Contains(Permissions.ProjectsManage);

    private async Task<bool> ProjectExistsAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken) =>
        await projects.FindAsync(organizationId, projectId, cancellationToken) is not null;
}
