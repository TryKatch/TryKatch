using System.Security.Claims;
using TrykatchApp.Api.Security;
using TrykatchApp.Application.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TrykatchApp.Api.Controllers;

[ApiController]
[Authorize]
[PlatformDataScoped]
[Route("api/v1/workspace")]
public sealed class WorkspaceContextController(
    IOrganizationAccessResolver resolver,
    IWorkspaceContextCookie workspaceCookie,
    IOrganizationContext organizationContext) : ControllerBase
{
    [HttpPost("select", Name = "Workspace_Select")]
    [CookieAntiforgery]
    public async Task<IActionResult> Select(SelectWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out Guid actorId)) return Unauthorized();
        OrganizationAccess? access = await resolver.ResolveAsync(actorId, request.OrganizationId, cancellationToken);
        if (access is null) return Problem(statusCode: 403, title: "Workspace access denied");

        workspaceCookie.Write(HttpContext, access.OrganizationId, request.Remember);
        return NoContent();
    }

    [HttpGet("current", Name = "Workspace_Current")]
    [OrganizationScoped]
    public ActionResult<CurrentWorkspaceResponse> Current() => Ok(new CurrentWorkspaceResponse(
        organizationContext.OrganizationId,
        organizationContext.MembershipId,
        organizationContext.Permissions.Order(StringComparer.Ordinal).ToArray()));

    [HttpDelete("current", Name = "Workspace_Clear")]
    [CookieAntiforgery]
    public IActionResult Clear()
    {
        workspaceCookie.Clear(HttpContext);
        return NoContent();
    }

    private bool TryGetActorId(out Guid actorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out actorId);
}

public sealed record SelectWorkspaceRequest(Guid OrganizationId, bool Remember = true);
public sealed record CurrentWorkspaceResponse(Guid OrganizationId, Guid MembershipId, IReadOnlyList<string> Permissions);
