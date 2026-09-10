using TrykatchApp.Application.Authorization;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Infrastructure.Organizations;

internal sealed class OrganizationAccessResolver(OrganizationControlPlaneDbContext dbContext, IPermissionCatalog permissionCatalog) : IOrganizationAccessResolver
{
    public async Task<OrganizationAccess?> ResolveAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Membership resolution requires an actor-scoped organization transaction.");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId.ToString()}, true), set_config('app.actor_id', {actorId.ToString()}, true)", cancellationToken);
        Organization? organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == organizationId && x.IsActive, cancellationToken);

        if (organization is null)
        {
            return null;
        }

        Membership? membership = await dbContext.Memberships
            .AsNoTracking()
            .Include(x => x.Roles)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organization.Id
                     && x.UserId == actorId
                     && x.Status == MembershipStatus.Active
                     && x.ArchivedAt == null
                     && x.DeletedAt == null,
                cancellationToken);

        if (membership is null)
        {
            return null;
        }

        Guid[] roleIds = membership.Roles.Select(x => x.RoleId).ToArray();
        string[] permissions = await dbContext.Roles
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && roleIds.Contains(x.Id) && x.ArchivedAt == null && x.DeletedAt == null)
            .SelectMany(x => x.Permissions)
            .Select(x => x.Permission)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        return new OrganizationAccess(
            organization.Id,
            organization.Slug,
            actorId,
            membership.Id,
            permissions.Where(permissionCatalog.Contains).ToHashSet(StringComparer.Ordinal));
    }
}
