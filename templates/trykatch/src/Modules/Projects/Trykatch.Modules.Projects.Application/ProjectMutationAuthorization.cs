using Trykatch.Application.Authorization;
using Trykatch.Modules;

namespace Trykatch.Modules.Projects.Application;

/// <summary>Authorizes idempotent mutation requests against the current Projects organization.</summary>
public sealed class ProjectMutationAuthorization(IProjectStore projects, IOrganizationFeatureEvaluator featureEvaluator)
{
    public async Task<bool> AuthorizeRequestAsync(MutationAuthority authority, Guid? projectId, CancellationToken cancellationToken)
    {
        if (!HasManagePermission(authority))
            return false;

        if (projectId == Guid.Empty)
            return false;

        if (!await featureEvaluator.IsEnabledAsync(ProjectFeatures.Writes, cancellationToken))
            throw new OrganizationFeatureUnavailableException();

        if (projectId is null)
            return true;

        return await ProjectExistsAsync(authority.OrganizationId, projectId.Value, cancellationToken);
    }

    public async Task<bool> AuthorizeReplayAsync(MutationAuthority authority, MutationResourceReference? resource, CancellationToken cancellationToken)
    {
        if (!HasManagePermission(authority)
            || resource is null
            || !string.Equals(resource.ResourceKey, "projects", StringComparison.Ordinal)
            || resource.ResourceId == Guid.Empty)
        {
            return false;
        }

        if (!await featureEvaluator.IsEnabledAsync(ProjectFeatures.Writes, cancellationToken))
            throw new OrganizationFeatureUnavailableException();

        return await ProjectExistsAsync(authority.OrganizationId, resource.ResourceId, cancellationToken);
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
