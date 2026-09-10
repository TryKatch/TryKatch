using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;

namespace Trykatch.Application.Organizations;

public sealed record OrganizationRoleSeeds(Guid OwnerRoleId, Guid AdministratorRoleId, Guid MemberRoleId, Guid ViewerRoleId);
public sealed record OrganizationCreationPreparation(
    Organization Organization,
    OrganizationCreationIntent Intent,
    Guid OwnerRoleId);
public sealed record OrganizationCreationPreparationResult(
    OrganizationCreationPreparation? Preparation,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsPrepared => Preparation is not null;
}

public interface IActorOrganizationDirectory
{
    Task<IReadOnlyList<Organization>> ListForUserAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IOrganizationDirectory
{
    Task<PagedResult<Organization>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken);
    Task<Organization?> FindAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
    Task<Guid> GetOwnerRoleIdAsync(Guid organizationId, CancellationToken cancellationToken);
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);
    Task<OrganizationCreationPreparationResult> PrepareCreationAsync(
        string name,
        string slug,
        Guid initiatingActorId,
        string administratorEmail,
        OrganizationDataPlacementKind placement,
        CancellationToken cancellationToken);
    Task AddAsync(Organization organization, CancellationToken cancellationToken);
    Task<OrganizationRoleSeeds> SeedRolesAsync(Guid organizationId, CancellationToken cancellationToken);
    Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken);
    Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Protects the recoverable invitation token held by a creation intent.</summary>
public interface IOrganizationInvitationTokenProtector
{
    string Protect(string token);
    string Unprotect(string protectedToken);
}
