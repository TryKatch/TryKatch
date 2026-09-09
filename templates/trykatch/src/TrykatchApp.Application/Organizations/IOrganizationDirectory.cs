using TrykatchApp.Application.Common;
using TrykatchApp.Domain.Organizations;

namespace TrykatchApp.Application.Organizations;

public sealed record OrganizationRoleSeeds(Guid OwnerRoleId, Guid AdministratorRoleId, Guid MemberRoleId, Guid ViewerRoleId);

public interface IOrganizationDirectory
{
    Task<PagedResult<Organization>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken);
    Task<Organization?> FindAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Organization>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);
    Task AddAsync(Organization organization, CancellationToken cancellationToken);
    Task<OrganizationRoleSeeds> SeedRolesAsync(Guid organizationId, CancellationToken cancellationToken);
    Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken);
    Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
