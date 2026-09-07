using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Common;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Domain.Organizations;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlatpackApp.Infrastructure.Organizations;

internal sealed class OrganizationDirectory(PlatformDbContext dbContext, IPermissionCatalog permissionCatalog) : IOrganizationDirectory
{
    public async Task<PagedResult<Organization>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken)
    {
        IQueryable<Organization> query = dbContext.Organizations.AsNoTracking().OrderBy(x => x.Name);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{search}%") || EF.Functions.ILike(x.Slug, $"%{search}%"));
        }

        long count = await query.LongCountAsync(cancellationToken);
        Organization[] items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        return new PagedResult<Organization>(items, page, pageSize, count);
    }

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        dbContext.Organizations.AnyAsync(x => x.Slug == slug, cancellationToken);

    public Task<Organization?> FindAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);

    public async Task<IReadOnlyList<Organization>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await (from organization in dbContext.Organizations.AsNoTracking()
               join membership in dbContext.Memberships.AsNoTracking()
                   on organization.Id equals membership.OrganizationId
               where membership.UserId == userId
                     && membership.Status == MembershipStatus.Active
                     && membership.ArchivedAt == null
                     && membership.DeletedAt == null
                     && organization.IsActive
               orderby organization.Name
               select organization)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(Organization organization, CancellationToken cancellationToken) =>
        await dbContext.Organizations.AddAsync(organization, cancellationToken);

    public async Task<OrganizationRoleSeeds> SeedRolesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Role owner = Role.Create(organizationId, "Owner", true);
        owner.SetPermissions(permissionCatalog.Keys);

        Role admin = Role.Create(organizationId, "Admin", true);
        admin.SetPermissions(permissionCatalog.Keys.Where(x => x != Permissions.OrganizationsManage));

        Role member = Role.Create(organizationId, "Member", true);
        member.SetPermissions([Permissions.MembersRead, Permissions.RolesRead, Permissions.ProjectsRead, Permissions.ProjectsManage]);

        Role viewer = Role.Create(organizationId, "Viewer", true);
        viewer.SetPermissions([Permissions.MembersRead, Permissions.RolesRead, Permissions.ProjectsRead]);

        await dbContext.Roles.AddRangeAsync([owner, admin, member, viewer], cancellationToken);
        return new OrganizationRoleSeeds(owner.Id, admin.Id, member.Id, viewer.Id);
    }

    public async Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken) =>
        await dbContext.Memberships.AddAsync(membership, cancellationToken);

    public async Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken) =>
        await dbContext.Invitations.AddAsync(invitation, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
