using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using FlatpackApp.Application.Auditing;
using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Common;
using FlatpackApp.Application.Identity;
using FlatpackApp.Application.Projects;
using FlatpackApp.Domain.Organizations;

namespace FlatpackApp.Application.Organizations;

public sealed record RoleDto(Guid Id, string Name, bool IsSystem, bool CanAssign, IReadOnlyList<string> Permissions, RecordLifecycleDto Lifecycle);
public sealed record PermissionOptionDto(string Key, string Name, string Description, bool IsSensitive, bool CanGrant);
public sealed record PermissionModuleDto(string Key, string Name, string Description, IReadOnlyList<PermissionOptionDto> Permissions);
public sealed record MemberDto(Guid Id, Guid UserId, string Email, string DisplayName, string Status, DateTimeOffset JoinedAt, IReadOnlyList<RoleDto> Roles, RecordLifecycleDto Lifecycle);
public sealed record InvitationDto(Guid Id, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status, RecordLifecycleDto Lifecycle);
public sealed record AuditActorDto(Guid Id, string DisplayName, string Email);
public sealed record AuditTargetDto(string Type, string Id, string DisplayName);
public sealed record AuditDto(
    Guid Id,
    string Action,
    string Title,
    string Description,
    string Category,
    string Severity,
    AuditActorDto Actor,
    AuditTargetDto Target,
    IReadOnlyDictionary<string, string?> Details,
    DateTimeOffset OccurredAt);
public sealed record AuditActionFilterDto(string Value, string Label, string Category);
public sealed record AuditActorFilterDto(Guid Value, string Label, string Email);
public sealed record AuditFilterOptionsDto(
    IReadOnlyList<AuditActionFilterDto> Actions,
    IReadOnlyList<string> SubjectTypes,
    IReadOnlyList<AuditActorFilterDto> Actors);
public sealed record AuditPageDto(
    IReadOnlyList<AuditDto> Items,
    int Page,
    int PageSize,
    long TotalCount,
    AuditFilterOptionsDto Filters);
public sealed record CreateInvitationCommand(string Email, int ExpiresInDays = 7);
public sealed record UpdateInvitationCommand(int ExpiresInDays);
public sealed record CreateInvitationResult(InvitationDto Invitation, string Token);
public sealed record InvitationPreviewDto(string Email, string OrganizationName, DateTimeOffset ExpiresAt);
public sealed record SaveRoleCommand(Guid? Id, string Name, IReadOnlyList<string> Permissions);
public sealed record UpdateMembershipCommand(Guid MembershipId, IReadOnlyList<Guid> RoleIds, bool IsActive);

public interface IOrganizationAdministrationStore
{
    Task<IReadOnlyList<Role>> ListRolesAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken);
    Task<Role?> FindRoleAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> RoleNameExistsAsync(Guid organizationId, string name, Guid? exceptRoleId, CancellationToken cancellationToken);
    Task AddRoleAsync(Role role, CancellationToken cancellationToken);
    Task<bool> RoleIsAssignedAsync(Guid organizationId, Guid roleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Membership>> ListMembershipsAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken);
    Task<Membership?> FindMembershipAsync(Guid organizationId, Guid membershipId, CancellationToken cancellationToken);
    Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken);
    Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken);
    Task<IReadOnlyList<Invitation>> ListInvitationsAsync(Guid organizationId, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken);
    Task<Invitation?> FindInvitationAsync(Guid organizationId, Guid invitationId, CancellationToken cancellationToken);
    Task<Invitation?> FindInvitationByHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task<bool> UsableInvitationExistsAsync(Guid organizationId, string email, DateTimeOffset now, CancellationToken cancellationToken);
    Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken);
    Task<Organization?> FindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class OrganizationAdministration(
    IOrganizationAdministrationStore store,
    IAuditReader auditReader,
    IUserDirectory users,
    IOrganizationContext context,
    IPermissionAuthorizer authorizer,
    IPermissionCatalog permissionCatalog,
    IAuditWriter auditWriter)
{
    public async Task<Result<IReadOnlyList<RoleDto>>> ListRolesAsync(RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.RolesRead, cancellationToken))
            return Forbidden<IReadOnlyList<RoleDto>>("Roles cannot be viewed by this membership.");
        return Result.Success<IReadOnlyList<RoleDto>>((await store.ListRolesAsync(context.OrganizationId, lifecycle, cancellationToken)).Select(ToRoleDto).ToArray());
    }

    public async Task<Result<RoleDto>> GetRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.RolesRead, cancellationToken))
            return Forbidden<RoleDto>("Roles cannot be viewed by this membership.");
        Role? role = await store.FindRoleAsync(context.OrganizationId, roleId, cancellationToken);
        return role is null ? Result.Failure<RoleDto>("not_found", "Role was not found.") : Result.Success(ToRoleDto(role));
    }

    public async Task<Result<IReadOnlyList<PermissionModuleDto>>> ListPermissionCatalogAsync(CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.RolesRead, cancellationToken))
            return Forbidden<IReadOnlyList<PermissionModuleDto>>("Permissions cannot be viewed by this membership.");

        PermissionModuleDto[] modules = permissionCatalog.Modules.Select(module => new PermissionModuleDto(
            module.Key,
            module.Name,
            module.Description,
            module.Permissions.Select(permission => new PermissionOptionDto(
                permission.Key,
                permission.Name,
                permission.Description,
                permission.IsSensitive,
                context.Permissions.Contains(permission.Key))).ToArray())).ToArray();
        return Result.Success<IReadOnlyList<PermissionModuleDto>>(modules);
    }

    public async Task<Result<RoleDto>> SaveRoleAsync(SaveRoleCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.RolesManage, cancellationToken))
            return Forbidden<RoleDto>("Roles cannot be changed by this membership.");
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Trim().Length > 80)
            return Result.Failure<RoleDto>("validation", "Role names must contain 1-80 characters.");
        IReadOnlyList<string> requestedPermissions = command.Permissions ?? [];
        if (requestedPermissions.Count != requestedPermissions.Distinct(StringComparer.Ordinal).Count())
            return Result.Failure<RoleDto>("validation", "Permission grants must be unique.");
        if (requestedPermissions.Any(permission => !permissionCatalog.Contains(permission)))
            return Result.Failure<RoleDto>("validation", "The role contains an unknown permission.");
        if (requestedPermissions.Any(permission => !context.Permissions.Contains(permission)))
            return Forbidden<RoleDto>("You cannot grant a permission that you do not hold.");
        if (await store.RoleNameExistsAsync(context.OrganizationId, command.Name.Trim(), command.Id, cancellationToken))
            return Result.Failure<RoleDto>("conflict", "A role with that name already exists.");

        Role role;
        if (command.Id is Guid roleId)
        {
            Role? existingRole = await store.FindRoleAsync(context.OrganizationId, roleId, cancellationToken);
            if (existingRole is null) return Result.Failure<RoleDto>("not_found", "Role was not found.");
            role = existingRole;
            if (role.IsSystem) return Result.Failure<RoleDto>("conflict", "System roles are immutable.");
            if (role.LifecycleState != FlatpackApp.Domain.Common.RecordLifecycleState.Active)
                return Result.Failure<RoleDto>("conflict", "Restore the role before editing it.");
            if (role.Permissions.Any(grant => !context.Permissions.Contains(grant.Permission)))
                return Forbidden<RoleDto>("You cannot change a role containing permissions that you do not hold.");
            role.Rename(command.Name);
        }
        else
        {
            role = Role.Create(context.OrganizationId, command.Name);
            await store.AddRoleAsync(role, cancellationToken);
        }

        role.SetPermissions(requestedPermissions);
        auditWriter.Record(
            command.Id is null ? AuditActions.RoleCreated : AuditActions.RoleUpdated,
            new AuditTarget("Role", role.Id.ToString(), role.Name),
            new Dictionary<string, string?> { ["permissionCount"] = requestedPermissions.Count.ToString(CultureInfo.InvariantCulture) });
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(ToRoleDto(role));
    }

    public Task<Result<bool>> ArchiveRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        ChangeRoleLifecycleAsync(roleId, "archive", null, cancellationToken);

    public Task<Result<bool>> RestoreRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        ChangeRoleLifecycleAsync(roleId, "restore", null, cancellationToken);

    public Task<Result<bool>> DeleteRoleAsync(Guid roleId, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        ChangeRoleLifecycleAsync(roleId, "delete", command.Reason, cancellationToken);

    public async Task<Result<IReadOnlyList<MemberDto>>> ListMembersAsync(RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersRead, cancellationToken))
            return Forbidden<IReadOnlyList<MemberDto>>("Members cannot be viewed by this membership.");
        IReadOnlyList<Membership> memberships = await store.ListMembershipsAsync(context.OrganizationId, lifecycle, cancellationToken);
        IReadOnlyList<Role> roles = await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.All, cancellationToken);
        IReadOnlyDictionary<Guid, UserSummary> profiles = await users.GetUsersAsync(memberships.Select(x => x.UserId), cancellationToken);
        Dictionary<Guid, Role> roleLookup = roles.ToDictionary(x => x.Id);
        MemberDto[] result = memberships.Select(membership =>
        {
            profiles.TryGetValue(membership.UserId, out UserSummary? profile);
            RoleDto[] assignedRoles = membership.Roles
                .Where(link => roleLookup.ContainsKey(link.RoleId))
                .Select(link => ToRoleDto(roleLookup[link.RoleId]))
                .ToArray();
            return new MemberDto(membership.Id, membership.UserId, profile?.Email ?? string.Empty, profile?.DisplayName ?? "Unknown user", membership.Status.ToString(), membership.JoinedAt, assignedRoles, RecordLifecycle.ToDto(membership));
        }).ToArray();
        return Result.Success<IReadOnlyList<MemberDto>>(result);
    }

    public async Task<Result<MemberDto>> GetMemberAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersRead, cancellationToken))
            return Forbidden<MemberDto>("Members cannot be viewed by this membership.");
        Membership? membership = await store.FindMembershipAsync(context.OrganizationId, membershipId, cancellationToken);
        if (membership is null) return Result.Failure<MemberDto>("not_found", "Membership was not found.");
        IReadOnlyList<Role> roles = await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.All, cancellationToken);
        UserSummary? profile = (await users.GetUsersAsync([membership.UserId], cancellationToken)).GetValueOrDefault(membership.UserId);
        return Result.Success(ToMemberDto(membership, roles, profile));
    }

    public async Task<Result<MemberDto>> UpdateMembershipAsync(UpdateMembershipCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<MemberDto>("Members cannot be changed by this membership.");
        if (command.MembershipId == context.MembershipId)
            return Result.Failure<MemberDto>("conflict", "You cannot change your own membership or assigned roles.");
        if (command.RoleIds.Count == 0)
            return Result.Failure<MemberDto>("validation", "At least one role is required.");

        Membership? membership = await store.FindMembershipAsync(context.OrganizationId, command.MembershipId, cancellationToken);
        if (membership is null) return Result.Failure<MemberDto>("not_found", "Membership was not found.");
        if (membership.LifecycleState != FlatpackApp.Domain.Common.RecordLifecycleState.Active)
            return Result.Failure<MemberDto>("conflict", "Restore the membership before editing it.");
        IReadOnlyList<Role> roles = await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.Active, cancellationToken);
        if (command.RoleIds.Distinct().Any(id => roles.All(role => role.Id != id)))
            return Result.Failure<MemberDto>("validation", "Every role must belong to this organization.");
        Role[] selectedRoles = roles.Where(role => command.RoleIds.Contains(role.Id)).ToArray();
        if (selectedRoles.SelectMany(role => role.Permissions).Any(grant => !context.Permissions.Contains(grant.Permission)))
            return Forbidden<MemberDto>("You cannot assign a role containing permissions that you do not hold.");

        membership.SetRoles(command.RoleIds);
        if (command.IsActive) membership.Activate(); else membership.Suspend();
        UserSummary? profile = (await users.GetUsersAsync([membership.UserId], cancellationToken)).GetValueOrDefault(membership.UserId);
        string memberDisplay = profile?.DisplayName ?? profile?.Email ?? $"Member {membership.Id.ToString()[..8]}";
        auditWriter.Record(
            AuditActions.MembershipUpdated,
            new AuditTarget("Membership", membership.Id.ToString(), memberDisplay),
            new Dictionary<string, string?>
            {
                ["status"] = membership.Status.ToString(),
                ["roles"] = string.Join(", ", selectedRoles.Select(x => x.Name).Order())
            });
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(ToMemberDto(membership, roles, profile));
    }

    public Task<Result<bool>> ArchiveMembershipAsync(Guid membershipId, CancellationToken cancellationToken) =>
        ChangeMembershipLifecycleAsync(membershipId, "archive", null, cancellationToken);

    public Task<Result<bool>> RestoreMembershipAsync(Guid membershipId, CancellationToken cancellationToken) =>
        ChangeMembershipLifecycleAsync(membershipId, "restore", null, cancellationToken);

    public Task<Result<bool>> DeleteMembershipAsync(Guid membershipId, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        ChangeMembershipLifecycleAsync(membershipId, "delete", command.Reason, cancellationToken);

    public async Task<Result<IReadOnlyList<InvitationDto>>> ListInvitationsAsync(RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersRead, cancellationToken))
            return Forbidden<IReadOnlyList<InvitationDto>>("Invitations cannot be viewed by this membership.");
        return Result.Success<IReadOnlyList<InvitationDto>>((await store.ListInvitationsAsync(context.OrganizationId, lifecycle, cancellationToken)).Select(ToInvitationDto).ToArray());
    }

    public async Task<Result<InvitationDto>> GetInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersRead, cancellationToken))
            return Forbidden<InvitationDto>("Invitations cannot be viewed by this membership.");
        Invitation? invitation = await store.FindInvitationAsync(context.OrganizationId, invitationId, cancellationToken);
        return invitation is null ? Result.Failure<InvitationDto>("not_found", "Invitation was not found.") : Result.Success(ToInvitationDto(invitation));
    }

    public async Task<Result<CreateInvitationResult>> CreateInvitationAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<CreateInvitationResult>("Invitations cannot be created by this membership.");
        string email = command.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@', StringComparison.Ordinal) || email.Length > 320 || command.ExpiresInDays is < 1 or > 30)
            return Result.Failure<CreateInvitationResult>("validation", "Provide a valid email and an expiry between 1 and 30 days.");
        Guid? userId = await users.FindUserIdByEmailAsync(email, cancellationToken);
        if (userId is Guid existingUser && await store.MembershipExistsAsync(context.OrganizationId, existingUser, cancellationToken))
            return Result.Failure<CreateInvitationResult>("conflict", "That user is already a member.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (await store.UsableInvitationExistsAsync(context.OrganizationId, email, now, cancellationToken))
            return Result.Failure<CreateInvitationResult>("conflict", "A usable invitation already exists for that email.");

        Role? memberRole = (await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.Active, cancellationToken)).SingleOrDefault(x => x.Name == "Member" && x.IsSystem);
        if (memberRole is null) return Result.Failure<CreateInvitationResult>("configuration", "The organization Member role is missing.");
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Invitation invitation = Invitation.Create(context.OrganizationId, memberRole.Id, email, HashToken(token), now.AddDays(command.ExpiresInDays));
        await store.AddInvitationAsync(invitation, cancellationToken);
        auditWriter.Record(AuditActions.InvitationCreated, new AuditTarget("Invitation", invitation.Id.ToString(), invitation.Email));
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(new CreateInvitationResult(ToInvitationDto(invitation), token));
    }

    public async Task<Result<InvitationDto>> UpdateInvitationAsync(Guid invitationId, UpdateInvitationCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<InvitationDto>("Invitations cannot be changed by this membership.");
        if (command.ExpiresInDays is < 1 or > 30)
            return Result.Failure<InvitationDto>("validation", "Invitation expiry must be between 1 and 30 days.");
        Invitation? invitation = await store.FindInvitationAsync(context.OrganizationId, invitationId, cancellationToken);
        if (invitation is null) return Result.Failure<InvitationDto>("not_found", "Invitation was not found.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!invitation.IsUsable(now)) return Result.Failure<InvitationDto>("conflict", "Only a pending invitation can be updated.");
        invitation.Reschedule(now, now.AddDays(command.ExpiresInDays));
        auditWriter.Record(
            AuditActions.InvitationUpdated,
            new AuditTarget("Invitation", invitation.Id.ToString(), invitation.Email),
            new Dictionary<string, string?> { ["expiresAt"] = invitation.ExpiresAt.ToString("O", CultureInfo.InvariantCulture) });
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(ToInvitationDto(invitation));
    }

    public async Task<Result<bool>> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<bool>("Invitations cannot be changed by this membership.");
        Invitation? invitation = await store.FindInvitationAsync(context.OrganizationId, invitationId, cancellationToken);
        if (invitation is null) return Result.Failure<bool>("not_found", "Invitation was not found.");
        if (invitation.AcceptedAt is not null) return Result.Failure<bool>("conflict", "An accepted invitation cannot be revoked.");
        invitation.Revoke(DateTimeOffset.UtcNow);
        auditWriter.Record(AuditActions.InvitationRevoked, new AuditTarget("Invitation", invitation.Id.ToString(), invitation.Email));
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    public Task<Result<bool>> RestoreInvitationAsync(Guid invitationId, CancellationToken cancellationToken) =>
        ChangeInvitationLifecycleAsync(invitationId, "restore", null, cancellationToken);

    public Task<Result<bool>> DeleteInvitationAsync(Guid invitationId, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        ChangeInvitationLifecycleAsync(invitationId, "delete", command.Reason, cancellationToken);

    public async Task<Result<AuditPageDto>> ListAuditAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.AuditRead, cancellationToken))
            return Forbidden<AuditPageDto>("Audit activity cannot be viewed by this membership.");

        AuditReadPage result = await auditReader.ListAsync(context.OrganizationId, query, cancellationToken);
        Guid[] userIds = result.Page.Items.Select(x => x.ActorId).Concat(result.ActorIds).Distinct().ToArray();
        IReadOnlyDictionary<Guid, UserSummary> actorProfiles = await users.GetUsersAsync(userIds, cancellationToken);
        AuditDto[] items = result.Page.Items.Select(entry => ToAuditDto(entry, actorProfiles.GetValueOrDefault(entry.ActorId))).ToArray();
        AuditActionFilterDto[] actions = AuditEventDefinitions.Describe(result.Actions)
            .Select(x => new AuditActionFilterDto(x.Action, x.Title, x.Category)).ToArray();
        AuditActorFilterDto[] actors = result.ActorIds.Select(actorId =>
        {
            UserSummary? profile = actorProfiles.GetValueOrDefault(actorId);
            return new AuditActorFilterDto(actorId, profile?.DisplayName ?? profile?.Email ?? $"User {actorId.ToString()[..8]}", profile?.Email ?? string.Empty);
        }).OrderBy(x => x.Label).ToArray();
        return Result.Success(new AuditPageDto(
            items,
            result.Page.Page,
            result.Page.PageSize,
            result.Page.TotalCount,
            new AuditFilterOptionsDto(actions, result.SubjectTypes, actors)));
    }

    public async Task<Result<Guid>> AcceptInvitationAsync(string token, Guid userId, string email, CancellationToken cancellationToken)
    {
        Invitation? invitation = await store.FindInvitationByHashAsync(HashToken(token), cancellationToken);
        if (invitation is null || !invitation.IsUsable(DateTimeOffset.UtcNow) || !string.Equals(invitation.Email, email.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result.Failure<Guid>("invalid_invitation", "Invitation is invalid, expired, or belongs to another account.");
        if (await store.MembershipExistsAsync(invitation.OrganizationId, userId, cancellationToken))
            return Result.Failure<Guid>("conflict", "The account is already a member.");
        Role? invitedRole = await store.FindRoleAsync(invitation.OrganizationId, invitation.RoleId, cancellationToken);
        if (invitedRole is null || invitedRole.LifecycleState != FlatpackApp.Domain.Common.RecordLifecycleState.Active)
            return Result.Failure<Guid>("configuration", "The invitation role is no longer available.");
        Membership membership = Membership.Create(invitation.OrganizationId, userId);
        membership.AssignRole(invitedRole.Id);
        invitation.Accept(DateTimeOffset.UtcNow);
        await store.AddMembershipAsync(membership, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        Organization? organization = await store.FindOrganizationAsync(invitation.OrganizationId, cancellationToken);
        return organization is null ? Result.Failure<Guid>("not_found", "Organization was not found.") : Result.Success(organization.Id);
    }

    public async Task<Result<InvitationPreviewDto>> PreviewInvitationAsync(string token, CancellationToken cancellationToken)
    {
        Invitation? invitation = await store.FindInvitationByHashAsync(HashToken(token), cancellationToken);
        if (invitation is null || !invitation.IsUsable(DateTimeOffset.UtcNow))
            return Result.Failure<InvitationPreviewDto>("invalid_invitation", "Invitation is invalid or has expired.");
        Organization? organization = await store.FindOrganizationAsync(invitation.OrganizationId, cancellationToken);
        return organization is null || !organization.IsActive
            ? Result.Failure<InvitationPreviewDto>("invalid_invitation", "The destination workspace is not available.")
            : Result.Success(new InvitationPreviewDto(invitation.Email, organization.Name, invitation.ExpiresAt));
    }

    private async Task<Result<bool>> ChangeRoleLifecycleAsync(Guid roleId, string operation, string? reason, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.RolesManage, cancellationToken))
            return Forbidden<bool>("Roles cannot be changed by this membership.");
        Role? role = await store.FindRoleAsync(context.OrganizationId, roleId, cancellationToken);
        if (role is null) return Result.Failure<bool>("not_found", "Role was not found.");
        if (role.IsSystem) return Result.Failure<bool>("conflict", "System roles cannot be archived or deleted.");
        if (role.Permissions.Any(grant => !context.Permissions.Contains(grant.Permission)))
            return Forbidden<bool>("You cannot change a role containing permissions that you do not hold.");
        if ((operation is "archive" or "delete") && await store.RoleIsAssignedAsync(context.OrganizationId, roleId, cancellationToken))
            return Result.Failure<bool>("conflict", "Remove this role from every member before archiving or deleting it.");
        if (operation == "delete" && role.LifecycleState != FlatpackApp.Domain.Common.RecordLifecycleState.Archived)
            return Result.Failure<bool>("conflict", "Archive the role before requesting deletion.");
        if (operation == "delete" && RecordLifecycle.ValidateDeletionReason(reason) is string validationError)
            return Result.Failure<bool>("validation", validationError);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = operation switch
        {
            "archive" => role.Archive(context.ActorId, now),
            "restore" => role.Restore(),
            "delete" => role.Delete(context.ActorId, reason!, now),
            _ => false
        };
        if (!changed) return Result.Success(true);
        string action = operation switch
        {
            "archive" => AuditActions.RoleArchived,
            "restore" => AuditActions.RoleRestored,
            _ => AuditActions.RoleDeleted
        };
        auditWriter.Record(action, new AuditTarget("Role", role.Id.ToString(), role.Name), operation == "delete" ? new Dictionary<string, string?> { ["reason"] = role.DeletionReason } : null);
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    private async Task<Result<bool>> ChangeMembershipLifecycleAsync(Guid membershipId, string operation, string? reason, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<bool>("Members cannot be changed by this membership.");
        if (membershipId == context.MembershipId)
            return Result.Failure<bool>("conflict", "You cannot archive or delete your own membership.");
        Membership? membership = await store.FindMembershipAsync(context.OrganizationId, membershipId, cancellationToken);
        if (membership is null) return Result.Failure<bool>("not_found", "Membership was not found.");
        if (operation == "delete" && membership.LifecycleState != FlatpackApp.Domain.Common.RecordLifecycleState.Archived)
            return Result.Failure<bool>("conflict", "Archive the membership before requesting deletion.");
        if (operation == "delete" && RecordLifecycle.ValidateDeletionReason(reason) is string validationError)
            return Result.Failure<bool>("validation", validationError);
        IReadOnlyList<Role> roles = await store.ListRolesAsync(context.OrganizationId, RecordLifecycleFilter.All, cancellationToken);
        Role[] assignedRoles = roles.Where(role => membership.Roles.Any(link => link.RoleId == role.Id)).ToArray();
        if (assignedRoles.SelectMany(role => role.Permissions).Any(grant => !context.Permissions.Contains(grant.Permission)))
            return Forbidden<bool>("You cannot change a member whose access exceeds your permission boundary.");
        UserSummary? profile = (await users.GetUsersAsync([membership.UserId], cancellationToken)).GetValueOrDefault(membership.UserId);
        string displayName = profile?.DisplayName ?? profile?.Email ?? $"Member {membership.Id.ToString()[..8]}";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = operation switch
        {
            "archive" => membership.Archive(context.ActorId, now),
            "restore" => membership.Restore(),
            "delete" => membership.Delete(context.ActorId, reason!, now),
            _ => false
        };
        if (!changed) return Result.Success(true);
        string action = operation switch
        {
            "archive" => AuditActions.MembershipArchived,
            "restore" => AuditActions.MembershipRestored,
            _ => AuditActions.MembershipDeleted
        };
        auditWriter.Record(action, new AuditTarget("Membership", membership.Id.ToString(), displayName), operation == "delete" ? new Dictionary<string, string?> { ["reason"] = membership.DeletionReason } : null);
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    private async Task<Result<bool>> ChangeInvitationLifecycleAsync(Guid invitationId, string operation, string? reason, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.MembersManage, cancellationToken))
            return Forbidden<bool>("Invitations cannot be changed by this membership.");
        Invitation? invitation = await store.FindInvitationAsync(context.OrganizationId, invitationId, cancellationToken);
        if (invitation is null) return Result.Failure<bool>("not_found", "Invitation was not found.");
        if (operation == "delete" && RecordLifecycle.ValidateDeletionReason(reason) is string validationError)
            return Result.Failure<bool>("validation", validationError);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = operation switch
        {
            "restore" => invitation.Restore(),
            "delete" => invitation.Delete(context.ActorId, reason!, now),
            _ => false
        };
        if (!changed) return Result.Success(true);
        string action = operation switch
        {
            "restore" => AuditActions.InvitationRestored,
            _ => AuditActions.InvitationDeleted
        };
        auditWriter.Record(action, new AuditTarget("Invitation", invitation.Id.ToString(), invitation.Email), operation == "delete" ? new Dictionary<string, string?> { ["reason"] = invitation.DeletionReason } : null);
        await store.SaveChangesAsync(cancellationToken);
        await auditWriter.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    private RoleDto ToRoleDto(Role role) => new(
        role.Id,
        role.Name,
        role.IsSystem,
        role.Permissions.All(grant => context.Permissions.Contains(grant.Permission)),
        role.Permissions.Select(x => x.Permission).Order().ToArray(),
        RecordLifecycle.ToDto(role));
    private MemberDto ToMemberDto(Membership membership, IReadOnlyList<Role> roles, UserSummary? profile) => new(
        membership.Id,
        membership.UserId,
        profile?.Email ?? string.Empty,
        profile?.DisplayName ?? "Unknown user",
        membership.Status.ToString(),
        membership.JoinedAt,
        roles.Where(role => membership.Roles.Any(link => link.RoleId == role.Id)).Select(ToRoleDto).ToArray(),
        RecordLifecycle.ToDto(membership));
    private static InvitationDto ToInvitationDto(Invitation invitation) => new(
        invitation.Id,
        invitation.Email,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.AcceptedAt is not null ? "Accepted" : invitation.RevokedAt is not null ? "Revoked" : invitation.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : "Pending",
        RecordLifecycle.ToDto(invitation));
    private static AuditDto ToAuditDto(AuditEntry entry, UserSummary? actor)
    {
        AuditEventDefinition definition = AuditEventDefinitions.Resolve(entry.Action);
        string actorName = actor?.DisplayName ?? actor?.Email ?? $"User {entry.ActorId.ToString()[..8]}";
        string targetName = string.IsNullOrWhiteSpace(entry.SubjectDisplayName)
            ? $"{entry.SubjectType} {ShortId(entry.SubjectId)}"
            : entry.SubjectDisplayName;
        string description = definition.DescriptionTemplate
            .Replace("{actor}", actorName, StringComparison.Ordinal)
            .Replace("{target}", targetName, StringComparison.Ordinal);
        IReadOnlyDictionary<string, string?> details;
        try
        {
            details = JsonSerializer.Deserialize<Dictionary<string, string?>>(entry.Details) ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            details = new Dictionary<string, string?>();
        }
        return new AuditDto(
            entry.Id,
            entry.Action,
            definition.Title,
            description,
            definition.Category,
            definition.Severity,
            new AuditActorDto(entry.ActorId, actorName, actor?.Email ?? string.Empty),
            new AuditTargetDto(entry.SubjectType, entry.SubjectId, targetName),
            details,
            entry.OccurredAt);
    }

    private static string ShortId(string value) => value.Length > 8 ? value[..8] : value;
    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static Result<T> Forbidden<T>(string message) => Result.Failure<T>("forbidden", message);
}
