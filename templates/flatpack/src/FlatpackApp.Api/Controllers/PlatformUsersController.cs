using System.Security.Claims;
using FlatpackApp.Api.Security;
using FlatpackApp.Application.Common;
using FlatpackApp.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[Route("api/v1/platform-users")]
[RequirePlatformPermission(PlatformPermissions.UsersRead)]
public sealed class PlatformUsersController(IPlatformAccessDirectory directory) : ControllerBase
{
    [HttpGet(Name = "PlatformUsers_List")]
    public async Task<ActionResult<PagedResult<PlatformAccessUser>>> List(
        int page = 1,
        int pageSize = 25,
        string? search = null,
        CancellationToken cancellationToken = default) =>
        Ok(await directory.ListAsync(Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search, cancellationToken));

    [HttpGet("roles", Name = "PlatformUsers_ListRoles")]
    public async Task<ActionResult<IReadOnlyList<PlatformRoleDefinition>>> ListRoles(CancellationToken cancellationToken)
    {
        IReadOnlySet<string> grantBoundary = GetGrantBoundary();
        PlatformRoleDefinition[] roleOptions = (await directory.ListRolesAsync(cancellationToken)).Select(role => role with
        {
            CanAssign = PlatformAccessRules.CanAssign(role, grantBoundary)
        }).ToArray();
        return Ok(roleOptions);
    }

    [HttpGet("permissions", Name = "PlatformUsers_ListPermissions")]
    public ActionResult<IReadOnlyList<PlatformPermissionModuleDefinition>> ListPermissions()
    {
        IReadOnlySet<string> grantBoundary = GetGrantBoundary();
        return Ok(PlatformPermissions.Modules.Select(module => module with
        {
            Permissions = module.Permissions.Select(permission => permission with
            {
                CanGrant = grantBoundary.Contains(permission.Key)
            }).ToArray()
        }).ToArray());
    }

    [HttpPost("roles", Name = "PlatformUsers_CreateRole")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformRoleDefinition>> CreateRole(SavePlatformRoleRequest request, CancellationToken cancellationToken)
    {
        Result<PlatformRoleDefinition> result = await directory.CreateRoleAsync(new(request.Name, request.Description, request.Permissions), GetGrantBoundary(), cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? CreatedAtAction(nameof(ListRoles), result.Value)
            : ToProblem(result);
    }

    [HttpPut("roles/{roleKey}", Name = "PlatformUsers_UpdateRole")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformRoleDefinition>> UpdateRole(string roleKey, SavePlatformRoleRequest request, CancellationToken cancellationToken) =>
        ToRoleActionResult(await directory.UpdateRoleAsync(roleKey, new(request.Name, request.Description, request.Permissions), GetGrantBoundary(), cancellationToken));

    [HttpDelete("roles/{roleKey}", Name = "PlatformUsers_DeleteRole")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<IActionResult> DeleteRole(string roleKey, CancellationToken cancellationToken)
    {
        Result<bool> result = await directory.DeleteRoleAsync(roleKey, GetGrantBoundary(), cancellationToken);
        return result.IsSuccess ? NoContent() : ToProblem(result);
    }

    [HttpGet("{userId:guid}", Name = "PlatformUsers_Get")]
    public async Task<ActionResult<PlatformAccessUser>> Get(Guid userId, CancellationToken cancellationToken) =>
        ToActionResult(await directory.GetAsync(userId, cancellationToken));

    [HttpPost(Name = "PlatformUsers_Grant")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformAccessGrant>> Grant(GrantPlatformAccessRequest request, CancellationToken cancellationToken)
    {
        Result<PlatformAccessGrant> result = await directory.GrantAsync(
            new GrantPlatformAccessCommand(request.Email, request.DisplayName, request.RoleKey),
            GetGrantBoundary(),
            cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? CreatedAtAction(nameof(Get), new { userId = result.Value.User.Id }, result.Value)
            : ToProblem(result);
    }

    [HttpPut("{userId:guid}/role", Name = "PlatformUsers_ChangeRole")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformAccessUser>> ChangeRole(Guid userId, ChangePlatformRoleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await directory.ChangeRoleAsync(GetActorId(), userId, request.RoleKey, GetGrantBoundary(), cancellationToken));

    [HttpPost("{userId:guid}/suspend", Name = "PlatformUsers_Suspend")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformAccessUser>> Suspend(Guid userId, CancellationToken cancellationToken) =>
        ToActionResult(await directory.SetStatusAsync(GetActorId(), userId, false, cancellationToken));

    [HttpPost("{userId:guid}/reactivate", Name = "PlatformUsers_Reactivate")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformAccessUser>> Reactivate(Guid userId, CancellationToken cancellationToken) =>
        ToActionResult(await directory.SetStatusAsync(GetActorId(), userId, true, cancellationToken));

    [HttpPost("{userId:guid}/activation-token", Name = "PlatformUsers_CreateActivationToken")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<ActionResult<PlatformActivationTokenResponse>> CreateActivationToken(Guid userId, CancellationToken cancellationToken)
    {
        Result<string> result = await directory.CreateActivationTokenAsync(userId, cancellationToken);
        return result.IsSuccess && result.Value is not null
            ? Ok(new PlatformActivationTokenResponse(userId, result.Value))
            : ToProblem(result);
    }

    [HttpDelete("{userId:guid}", Name = "PlatformUsers_Revoke")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.UsersManage)]
    public async Task<IActionResult> Revoke(Guid userId, CancellationToken cancellationToken)
    {
        Result<bool> result = await directory.RevokeAsync(GetActorId(), userId, cancellationToken);
        return result.IsSuccess ? NoContent() : ToProblem(result);
    }

    private Guid GetActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actorId)
            ? actorId
            : throw new InvalidOperationException("Authenticated platform actor does not have a valid identifier.");

    private IReadOnlySet<string> GetGrantBoundary() =>
        User.HasClaim("platform_admin", "true")
            ? PlatformPermissions.All
            : User.FindAll("platform_permission")
                .Select(claim => claim.Value)
                .Where(PlatformPermissions.All.Contains)
                .ToHashSet(StringComparer.Ordinal);

    private ActionResult<PlatformAccessUser> ToActionResult(Result<PlatformAccessUser> result) =>
        result.IsSuccess && result.Value is not null ? Ok(result.Value) : ToProblem(result);

    private ActionResult<PlatformRoleDefinition> ToRoleActionResult(Result<PlatformRoleDefinition> result) =>
        result.IsSuccess && result.Value is not null ? Ok(result.Value) : ToProblem(result);

    private ObjectResult ToProblem<T>(Result<T> result) => Problem(
        statusCode: result.ErrorCode switch
        {
            "not_found" => StatusCodes.Status404NotFound,
            "access_exists" or "duplicate_role" => StatusCodes.Status409Conflict,
            "self_change" or "last_administrator" or "role_in_use" or "system_role" => StatusCodes.Status409Conflict,
            "grant_boundary" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        },
        title: result.ErrorCode,
        detail: result.ErrorMessage);
}

[ApiController]
[Route("api/v1/access-activation")]
public sealed class PlatformAccessActivationController(IPlatformAccessDirectory directory) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost(Name = "PlatformAccess_Activate")]
    [CookieAntiforgery]
    public async Task<IActionResult> Activate(ActivatePlatformAccessRequest request, CancellationToken cancellationToken)
    {
        Result<bool> result = await directory.ActivateAsync(new(request.UserId, request.Token, request.Password), cancellationToken);
        return result.IsSuccess
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: result.ErrorCode, detail: result.ErrorMessage);
    }
}

public sealed record GrantPlatformAccessRequest(string Email, string DisplayName, string RoleKey);
public sealed record ChangePlatformRoleRequest(string RoleKey);
public sealed record SavePlatformRoleRequest(string Name, string Description, IReadOnlyCollection<string> Permissions);
public sealed record PlatformActivationTokenResponse(Guid UserId, string Token);
public sealed record ActivatePlatformAccessRequest(Guid UserId, string Token, string Password);
