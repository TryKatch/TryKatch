using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationDirectory(
    PlatformDbContext dbContext,
    IPermissionCatalog permissionCatalog,
    TimeProvider timeProvider) : IOrganizationDirectory
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

    public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        dbContext.Organizations.SingleOrDefaultAsync(x => x.Slug == slug, cancellationToken);

    public async Task<OrganizationCreationPreparationResult> PrepareCreationAsync(
        string name,
        string slug,
        Guid initiatingActorId,
        string administratorEmail,
        OrganizationDataPlacementKind placement,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization creation requires a platform transaction.");

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({slug}, 20260911))",
            cancellationToken);

        OrganizationCreationIntent? intent = await dbContext.OrganizationCreationIntents
            .SingleOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);
        if (intent is null)
        {
            if (await dbContext.Organizations.AnyAsync(candidate => candidate.Slug == slug, cancellationToken))
                return Conflict("An organization already uses this slug.");

            Organization organization = Organization.Create(name, slug);
            await dbContext.Organizations.AddAsync(organization, cancellationToken);
            OrganizationRoleSeeds roles = await SeedRolesAsync(organization.Id, cancellationToken);
            intent = OrganizationCreationIntent.Begin(
                organization.Id,
                slug,
                initiatingActorId,
                administratorEmail,
                placement,
                timeProvider.GetUtcNow());
            await dbContext.OrganizationCreationIntents.AddAsync(intent, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Prepared(organization, intent, roles.OwnerRoleId);
        }

        if (!intent.Matches(slug, initiatingActorId, administratorEmail, placement))
            return Conflict("The existing organization creation attempt has a different initiating actor or owner identity.");

        Organization existing = await dbContext.Organizations
            .SingleOrDefaultAsync(candidate => candidate.Id == intent.OrganizationId && candidate.Slug == intent.Slug, cancellationToken)
            ?? throw new InvalidOperationException("Organization creation intent refers to a missing or renamed organization.");
        Guid ownerRoleId = await GetOwnerRoleIdAsync(existing.Id, cancellationToken);
        return Prepared(existing, intent, ownerRoleId);
    }

    public async Task<Guid> GetOwnerRoleIdAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization provisioning recovery requires a platform transaction.");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId.ToString()}, true)", cancellationToken);
        return await dbContext.Roles
            .Where(role => role.OrganizationId == organizationId && role.IsSystem && role.Name == "Owner")
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);
    }

    public async Task AddAsync(Organization organization, CancellationToken cancellationToken) =>
        await dbContext.Organizations.AddAsync(organization, cancellationToken);

    public async Task<OrganizationRoleSeeds> SeedRolesAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Organization provisioning requires a platform transaction.");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationId.ToString()}, true)", cancellationToken);
        Role owner = Role.Create(organizationId, "Owner", "Full control of this workspace, including access management.", isSystem: true);
        owner.SetPermissions(permissionCatalog.Keys);

        Role admin = Role.Create(organizationId, "Admin", "Manage workspace operations, people, roles, and projects.", isSystem: true);
        admin.SetPermissions(permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Admin));

        Role member = Role.Create(organizationId, "Member", "Create and manage workspace projects.", isSystem: true);
        member.SetPermissions(permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Member));

        Role viewer = Role.Create(organizationId, "Viewer", "Read-only access to workspace projects.", isSystem: true);
        viewer.SetPermissions(permissionCatalog.GetDefaultsForRole(DefaultOrganizationRoles.Viewer));

        await dbContext.Roles.AddRangeAsync([owner, admin, member, viewer], cancellationToken);
        return new OrganizationRoleSeeds(owner.Id, admin.Id, member.Id, viewer.Id);
    }

    public async Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken) =>
        await dbContext.Memberships.AddAsync(membership, cancellationToken);

    public async Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken) =>
        await dbContext.Invitations.AddAsync(invitation, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

    private static OrganizationCreationPreparationResult Prepared(
        Organization organization,
        OrganizationCreationIntent intent,
        Guid ownerRoleId) =>
        new(new(organization, intent, ownerRoleId));

    private static OrganizationCreationPreparationResult Conflict(string message) =>
        new(null, "slug_conflict", message);
}
