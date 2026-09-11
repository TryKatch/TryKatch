using Trykatch.Application.Authorization;
using Trykatch.Application.Auditing;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize]
[OrganizationScoped]
[Route("api/v1")]
public sealed class OrganizationAdministrationController(
    OrganizationAdministration administration,
    IOrganizationContext context,
    IInvitationNotifier invitationNotifier,
    IApplicationUrlResolver applicationUrls,
    ILogger<OrganizationAdministrationController> logger) : ControllerBase
{
    private static readonly Action<ILogger, Guid, Exception?> LogInvitationDeliveryFailure =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1001, nameof(CreateInvitation)),
            "Invitation delivery failed for invitation {InvitationId}");

    [HttpGet("access", Name = "OrganizationAccess_Get")]
    public ActionResult<OrganizationAccessResponse> GetAccess()
    {
        return Ok(new OrganizationAccessResponse(context.OrganizationId, context.MembershipId, context.Permissions.Order(StringComparer.Ordinal).ToArray()));
    }

    [HttpGet("roles", Name = "Roles_List")]
    [RequirePermission(Permissions.RolesRead)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> ListRoles(string lifecycle = "active", CancellationToken cancellationToken = default)
    {
        if (!RecordLifecycle.TryParseFilter(lifecycle, out RecordLifecycleFilter filter))
            return ProblemResult("validation", "Lifecycle must be active, archived, deleted, recoverable, or all.");
        return ToActionResult(await administration.ListRolesAsync(filter, cancellationToken));
    }

    [HttpGet("roles/{id:guid}", Name = "Roles_Get")]
    [RequirePermission(Permissions.RolesRead)]
    public async Task<ActionResult<RoleDto>> GetRole(Guid id, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.GetRoleAsync(id, cancellationToken));
    }

    [HttpGet("permissions", Name = "Permissions_List")]
    [RequirePermission(Permissions.RolesRead)]
    public async Task<ActionResult<IReadOnlyList<PermissionModuleDto>>> ListPermissions(CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.ListPermissionCatalogAsync(cancellationToken));
    }

    [HttpPost("roles", Name = "Roles_Create")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleDto>> CreateRole(SaveRoleCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.SaveRoleAsync(command with { Id = null }, cancellationToken));
    }

    [HttpPut("roles/{id:guid}", Name = "Roles_Update")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.RolesManage)]
    public async Task<ActionResult<RoleDto>> UpdateRole(Guid id, SaveRoleCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.SaveRoleAsync(command with { Id = id }, cancellationToken));
    }

    [HttpPost("roles/{id:guid}/archive", Name = "Roles_Archive")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.RolesManage)]
    public Task<IActionResult> ArchiveRole(Guid id, CancellationToken cancellationToken) =>
        LifecycleResult(administration.ArchiveRoleAsync(id, cancellationToken));

    [HttpPost("roles/{id:guid}/restore", Name = "Roles_Restore")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.RolesManage)]
    public Task<IActionResult> RestoreRole(Guid id, CancellationToken cancellationToken) =>
        LifecycleResult(administration.RestoreRoleAsync(id, cancellationToken));

    [HttpDelete("roles/{id:guid}", Name = "Roles_Delete")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.RolesManage)]
    public Task<IActionResult> DeleteRole(Guid id, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        LifecycleResult(administration.DeleteRoleAsync(id, command, cancellationToken));

    [HttpGet("members", Name = "Members_List")]
    [RequirePermission(Permissions.MembersRead)]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> ListMembers(string lifecycle = "active", CancellationToken cancellationToken = default)
    {
        if (!RecordLifecycle.TryParseFilter(lifecycle, out RecordLifecycleFilter filter))
            return ProblemResult("validation", "Lifecycle must be active, archived, deleted, recoverable, or all.");
        return ToActionResult(await administration.ListMembersAsync(filter, cancellationToken));
    }

    [HttpGet("members/{id:guid}", Name = "Members_Get")]
    [RequirePermission(Permissions.MembersRead)]
    public async Task<ActionResult<MemberDto>> GetMember(Guid id, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.GetMemberAsync(id, cancellationToken));
    }

    [HttpPut("members/{id:guid}", Name = "Members_Update")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public async Task<ActionResult<MemberDto>> UpdateMember(Guid id, UpdateMembershipCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.UpdateMembershipAsync(command with { MembershipId = id }, cancellationToken));
    }

    [HttpPost("members/{id:guid}/archive", Name = "Members_Archive")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public Task<IActionResult> ArchiveMember(Guid id, CancellationToken cancellationToken) =>
        LifecycleResult(administration.ArchiveMembershipAsync(id, cancellationToken));

    [HttpPost("members/{id:guid}/restore", Name = "Members_Restore")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public Task<IActionResult> RestoreMember(Guid id, CancellationToken cancellationToken) =>
        LifecycleResult(administration.RestoreMembershipAsync(id, cancellationToken));

    [HttpDelete("members/{id:guid}", Name = "Members_Delete")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public Task<IActionResult> DeleteMember(Guid id, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        LifecycleResult(administration.DeleteMembershipAsync(id, command, cancellationToken));

    [HttpGet("invitations", Name = "Invitations_List")]
    [RequirePermission(Permissions.MembersRead)]
    public async Task<ActionResult<IReadOnlyList<InvitationDto>>> ListInvitations(string lifecycle = "active", CancellationToken cancellationToken = default)
    {
        if (!RecordLifecycle.TryParseFilter(lifecycle, out RecordLifecycleFilter filter))
            return ProblemResult("validation", "Lifecycle must be active, archived, deleted, recoverable, or all.");
        return ToActionResult(await administration.ListInvitationsAsync(filter, cancellationToken));
    }

    [HttpGet("invitations/{id:guid}", Name = "Invitations_Get")]
    [RequirePermission(Permissions.MembersRead)]
    public async Task<ActionResult<InvitationDto>> GetInvitation(Guid id, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.GetInvitationAsync(id, cancellationToken));
    }

    [HttpPost("invitations", Name = "Invitations_Create")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public async Task<ActionResult<CreateInvitationResponse>> CreateInvitation(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        Result<CreateInvitationResult> result = await administration.CreateInvitationAsync(command, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return ProblemResult(result.ErrorCode, result.ErrorMessage);
        }

        string invitationUrl = $"{applicationUrls.ResolveBaseUrl(Request)}/invite/{Uri.EscapeDataString(result.Value.Token)}";
        bool emailDelivered = false;
        if (invitationNotifier.IsConfigured)
        {
            try
            {
                await invitationNotifier.SendOrganizationInvitationAsync(
                    result.Value.Invitation.Email,
                    result.Value.OrganizationName,
                    invitationUrl,
                    cancellationToken);
                emailDelivered = true;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogInvitationDeliveryFailure(logger, result.Value.Invitation.Id, exception);
            }
        }

        return Ok(new CreateInvitationResponse(result.Value.Invitation, invitationUrl, emailDelivered));
    }

    [HttpPut("invitations/{id:guid}", Name = "Invitations_Update")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public async Task<ActionResult<InvitationDto>> UpdateInvitation(Guid id, UpdateInvitationCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await administration.UpdateInvitationAsync(id, command, cancellationToken));
    }

    [HttpPost("invitations/{id:guid}/revoke", Name = "Invitations_Revoke")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public async Task<IActionResult> RevokeInvitation(Guid id, CancellationToken cancellationToken)
    {
        Result<bool> result = await administration.RevokeInvitationAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ProblemResult(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("invitations/{id:guid}/restore", Name = "Invitations_Restore")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public Task<IActionResult> RestoreInvitation(Guid id, CancellationToken cancellationToken) =>
        LifecycleResult(administration.RestoreInvitationAsync(id, cancellationToken));

    [HttpDelete("invitations/{id:guid}", Name = "Invitations_Delete")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.MembersManage)]
    public Task<IActionResult> DeleteInvitation(Guid id, DeleteRecordCommand command, CancellationToken cancellationToken) =>
        LifecycleResult(administration.DeleteInvitationAsync(id, command, cancellationToken));

    [HttpGet("audit", Name = "Audit_List")]
    [RequirePermission(Permissions.AuditRead)]
    public async Task<ActionResult<AuditPageDto>> ListAudit(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? action = null,
        string? subjectType = null,
        Guid? actorId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string sortBy = "occurredAt",
        string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        AuditQuery query = new(
            Math.Max(1, page),
            Math.Clamp(pageSize, 1, 100),
            search,
            action,
            subjectType,
            actorId,
            from,
            to,
            sortBy,
            sortDirection);
        return ToActionResult(await administration.ListAuditAsync(query, cancellationToken));
    }

    private ActionResult<T> ToActionResult<T>(Result<T> result) =>
        result.IsSuccess && result.Value is not null ? Ok(result.Value) : ProblemResult(result.ErrorCode, result.ErrorMessage);

    private async Task<IActionResult> LifecycleResult(Task<Result<bool>> operation)
    {
        Result<bool> result = await operation;
        return result.IsSuccess ? NoContent() : ProblemResult(result.ErrorCode, result.ErrorMessage);
    }

    private ObjectResult ProblemResult(string? code, string? detail) =>
        Problem(statusCode: code switch { "forbidden" => 403, "not_found" => 404, "conflict" or "last_owner" => 409, _ => 400 }, title: code, detail: detail);
}

public sealed record CreateInvitationResponse(InvitationDto Invitation, string InvitationUrl, bool EmailDelivered);

public sealed record OrganizationAccessResponse(Guid OrganizationId, Guid MembershipId, IReadOnlyList<string> Permissions);
