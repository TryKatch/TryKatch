using System.Collections.Frozen;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Domain.Common;
using Trykatch.Domain.Organizations;

namespace Trykatch.Application.Organizations;

/// <summary>
/// Revalidates management authority after taking the organization's transaction-scoped lock.
/// The resulting decision is valid only within that transaction; session permission sets are not proof.
/// </summary>
public sealed class OrganizationManagementAuthorization(
    IOrganizationAdministrationStore store,
    IOrganizationContext context,
    IPermissionCatalog catalog)
{
    public async Task<Result<OrganizationManagementAuthority>> BeginAsync(string permission, CancellationToken cancellationToken)
    {
        if (!context.IsResolved)
            return Denied<OrganizationManagementAuthority>("An active organization membership is required.");
        await store.AcquireManagementLockAsync(context.OrganizationId, cancellationToken);
        Membership? actor = await store.FindMembershipForUserAsync(context.OrganizationId, context.ActorId, cancellationToken);
        if (actor is null || !IsActive(actor) || await store.FindOrganizationAsync(context.OrganizationId, cancellationToken) is null)
            return Denied<OrganizationManagementAuthority>("An active organization membership is required.");

        IReadOnlyList<Role> roles = await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.All, cancellationToken);
        Role[] assigned = roles.Where(role => role.LifecycleState == RecordLifecycleState.Active
            && actor.Roles.Any(link => link.RoleId == role.Id)).ToArray();
        FrozenSet<string> permissions = assigned.SelectMany(role => role.Permissions)
            .Select(grant => grant.Permission).Where(catalog.Contains).ToFrozenSet(StringComparer.Ordinal);
        if (!permissions.Contains(permission))
            return Denied<OrganizationManagementAuthority>("This membership no longer holds the required management permission.");
        return Result.Success(new OrganizationManagementAuthority(actor.UserId, assigned.Any(IsOwner), permissions, roles));
    }

    public async Task<Result<bool>> AuthorizeMembershipChangeAsync(
        OrganizationManagementAuthority authority,
        Membership membership,
        IReadOnlyList<Role> nextRoles,
        bool remainsActive,
        CancellationToken cancellationToken)
    {
        Role[] currentRoles = authority.Roles.Where(role => membership.Roles.Any(link => link.RoleId == role.Id)).ToArray();
        if (currentRoles.Length != membership.Roles.Count || !authority.CanManage(currentRoles) || !authority.CanManage(nextRoles))
            return Denied<bool>("You cannot manage this membership's current or proposed authority.");

        bool losesOwnership = !remainsActive || !nextRoles.Any(IsOwner);
        if (IsActive(membership) && currentRoles.Any(role => IsOwner(role) && role.LifecycleState == RecordLifecycleState.Active)
            && losesOwnership && !await store.HasOtherActiveOwnerAsync(context.OrganizationId, membership.Id, cancellationToken))
            return Result.Failure<bool>("last_owner", "At least one active organization Owner is required.");
        if (membership.UserId == authority.ActorId)
            return Result.Failure<bool>("conflict", "You cannot change your own membership or assigned roles.");
        return Result.Success(true);
    }

    internal static bool IsOwner(Role role) => role.IsSystem && role.Name == "Owner";
    private static bool IsActive(Membership membership) =>
        membership.Status == MembershipStatus.Active && membership.LifecycleState == RecordLifecycleState.Active;
    private static Result<T> Denied<T>(string message) => Result.Failure<T>("forbidden", message);
}

public sealed class OrganizationManagementAuthority
{
    internal OrganizationManagementAuthority(Guid actorId, bool isOwner, IReadOnlySet<string> permissions, IReadOnlyList<Role> roles)
    {
        ActorId = actorId;
        IsOwner = isOwner;
        Permissions = permissions;
        Roles = roles;
    }

    public Guid ActorId { get; }
    public bool IsOwner { get; }
    public IReadOnlySet<string> Permissions { get; }
    public IReadOnlyList<Role> Roles { get; }

    public bool CanManage(IEnumerable<Role> roles) => roles.All(role =>
        (!OrganizationManagementAuthorization.IsOwner(role) || IsOwner)
        && role.Permissions.All(grant => Permissions.Contains(grant.Permission)));

    public bool CanManageInvitation(Invitation invitation) =>
        Roles.SingleOrDefault(role => role.Id == invitation.RoleId) is Role role && CanManage([role]);
}
