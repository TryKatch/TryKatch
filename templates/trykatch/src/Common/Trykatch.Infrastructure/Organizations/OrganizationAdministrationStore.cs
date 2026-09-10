using Trykatch.Application.Organizations;
using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationAdministrationStore(OrganizationControlPlaneDbContext dbContext) : IOrganizationAdministrationStore
{
    public async Task<IReadOnlyList<Role>> ListRolesAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        IQueryable<Role> query = dbContext.Roles.AsNoTracking().Include(x => x.Permissions).Where(x => x.OrganizationId == organizationId);
        query = ApplyLifecycle(query, lifecycle);
        return await query.OrderByDescending(x => x.IsSystem).ThenBy(x => x.Name).ToArrayAsync(cancellationToken);
    }

    public Task<Role?> FindRoleAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken) =>
        dbContext.Roles.Include(x => x.Permissions).SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == roleId, cancellationToken);

    public Task<bool> RoleNameExistsAsync(Guid organizationId, string name, Guid? exceptRoleId, CancellationToken cancellationToken) =>
        dbContext.Roles.AnyAsync(x => x.OrganizationId == organizationId && EF.Functions.ILike(x.Name, name) && x.Id != exceptRoleId, cancellationToken);

    public async Task AddRoleAsync(Role role, CancellationToken cancellationToken) => await dbContext.Roles.AddAsync(role, cancellationToken);

    public Task<bool> RoleIsAssignedAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken) =>
        dbContext.Memberships.AnyAsync(x => x.OrganizationId == organizationId && x.DeletedAt == null && x.Roles.Any(link => link.RoleId == roleId), cancellationToken);

    public async Task<IReadOnlyList<Membership>> ListMembershipsAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        IQueryable<Membership> query = dbContext.Memberships.AsNoTracking().Include(x => x.Roles).Where(x => x.OrganizationId == organizationId);
        query = ApplyLifecycle(query, lifecycle);
        return await query.OrderBy(x => x.JoinedAt).ToArrayAsync(cancellationToken);
    }

    public Task<Membership?> FindMembershipAsync(Guid organizationId, Guid membershipId, CancellationToken cancellationToken) =>
        dbContext.Memberships.Include(x => x.Roles).SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == membershipId, cancellationToken);

    public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken) =>
        dbContext.Memberships.AnyAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);

    public async Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken) => await dbContext.Memberships.AddAsync(membership, cancellationToken);

    public async Task<IReadOnlyList<Invitation>> ListInvitationsAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        IQueryable<Invitation> query = dbContext.Invitations.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        query = ApplyLifecycle(query, lifecycle);
        return await query.OrderByDescending(x => x.CreatedAt).ToArrayAsync(cancellationToken);
    }

    public Task<Invitation?> FindInvitationAsync(Guid organizationId, Guid invitationId, CancellationToken cancellationToken) =>
        dbContext.Invitations.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == invitationId, cancellationToken);

    public async Task<Invitation?> FindInvitationByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        if (tokenHash.Length != 64 || tokenHash.Any(character => !Uri.IsHexDigit(character))) return null;
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.invitation_hash', {tokenHash}, true)", cancellationToken);
            return await dbContext.Invitations.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        }
        finally
        {
            if (transaction is null)
                await dbContext.Database.ExecuteSqlRawAsync("SELECT set_config('app.invitation_hash', '', true)", CancellationToken.None);
        }
    }

    public Task<bool> UsableInvitationExistsAsync(Guid organizationId, string email, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Invitations.AnyAsync(x => x.OrganizationId == organizationId && x.Email == email && x.AcceptedAt == null && x.RevokedAt == null && x.ExpiresAt > now && x.ArchivedAt == null && x.DeletedAt == null, cancellationToken);

    public async Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken) => await dbContext.Invitations.AddAsync(invitation, cancellationToken);

    public Task<Organization?> FindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId && x.IsActive, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

    private static IQueryable<T> ApplyLifecycle<T>(IQueryable<T> query, RecordLifecycleFilter lifecycle) where T : Trykatch.Domain.Common.RecoverableEntity =>
        lifecycle switch
        {
            RecordLifecycleFilter.Active => query.Where(x => x.ArchivedAt == null && x.DeletedAt == null),
            RecordLifecycleFilter.Archived => query.Where(x => x.ArchivedAt != null && x.DeletedAt == null),
            RecordLifecycleFilter.Deleted => query.Where(x => x.DeletedAt != null),
            RecordLifecycleFilter.Recoverable => query.Where(x => x.ArchivedAt != null || x.DeletedAt != null),
            _ => query
        };
}
