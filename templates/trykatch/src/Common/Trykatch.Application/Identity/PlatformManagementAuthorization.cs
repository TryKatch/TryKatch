using Trykatch.Application.Common;

namespace Trykatch.Application.Identity;

/// <summary>Reads current authority from the identity store, never from session permission claims.</summary>
public interface IPlatformAuthorityReader
{
    Task<EffectivePlatformAccess> ReadAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> HasOtherActiveAdministratorAsync(Guid exceptUserId, CancellationToken cancellationToken);
}

public enum PlatformManagementOperation
{
    ManageRole,
    Grant,
    ChangeRole,
    Suspend,
    Reactivate,
    IssueActivationToken,
    Revoke
}

/// <summary>
/// The application-level boundary for platform access management. Callers must serialize the
/// decision and mutation in the same identity transaction before reading actor or target authority.
/// </summary>
public sealed class PlatformManagementAuthorization(IPlatformAuthorityReader authority)
{
    public async Task<Result<EffectivePlatformAccess>> AuthorizeAsync(
        Guid actorId,
        PlatformManagementOperation operation,
        Guid? targetId,
        PlatformRoleDefinition? proposedRole,
        CancellationToken cancellationToken)
    {
        EffectivePlatformAccess actor = await authority.ReadAsync(actorId, cancellationToken);
        if (!actor.HasPermission(PlatformPermissions.UsersManage))
            return Denied("Active platform access-management authority is required.");

        EffectivePlatformAccess current = EffectivePlatformAccess.None;
        if (targetId is Guid target)
        {
            current = await authority.ReadAsync(target, cancellationToken);
            if (current.IsAdministrator && !actor.IsAdministrator)
                return Denied("Only an Administrator can manage Administrator access.");
            if (!PlatformAccessRules.CanGrant(current.Permissions, actor.Permissions))
                return Denied("You cannot manage someone whose current access exceeds your authority.");
        }

        if (proposedRole is not null
            && ((proposedRole.Key == PlatformRoles.Administrator && !actor.IsAdministrator)
                || !PlatformAccessRules.CanAssign(proposedRole, actor.Permissions)))
            return Denied("You cannot assign this platform role.");

        bool removesAccess = operation is PlatformManagementOperation.Suspend or PlatformManagementOperation.Revoke
            || (operation == PlatformManagementOperation.ChangeRole && proposedRole?.Key != PlatformRoles.Administrator);
        if (targetId is Guid targetUserId && current.IsActive && current.IsAdministrator && removesAccess
            && !await authority.HasOtherActiveAdministratorAsync(targetUserId, cancellationToken))
            return Result.Failure<EffectivePlatformAccess>("last_administrator", "At least one active platform administrator is required.");
        if (targetId == actorId && (operation is PlatformManagementOperation.Suspend or PlatformManagementOperation.Revoke
            || (operation == PlatformManagementOperation.ChangeRole && proposedRole?.Key != current.RoleKey)))
            return Result.Failure<EffectivePlatformAccess>("self_change", "You cannot change your own platform role or remove your own access.");

        return Result.Success(actor);
    }

    private static Result<EffectivePlatformAccess> Denied(string message) =>
        Result.Failure<EffectivePlatformAccess>("grant_boundary", message);
}
