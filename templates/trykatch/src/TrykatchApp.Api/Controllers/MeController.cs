using System.Security.Claims;
using TrykatchApp.Api.Security;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TrykatchApp.Api.Controllers;

[ApiController]
[Authorize]
[PlatformDataScoped]
[Route("api/v1/me")]
public sealed class MeController(IOrganizationDirectory organizations) : ControllerBase
{
    [HttpGet("organizations", Name = "Me_ListOrganizations")]
    public async Task<ActionResult<IReadOnlyList<MyOrganizationDto>>> ListOrganizations(CancellationToken cancellationToken)
    {
        string? subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out Guid userId))
        {
            return Unauthorized();
        }

        IReadOnlyList<Organization> memberships = await organizations.ListForUserAsync(userId, cancellationToken);
        return Ok(memberships.Select(x => new MyOrganizationDto(x.Id, x.Name, x.Slug)).ToArray());
    }
}

public sealed record MyOrganizationDto(Guid Id, string Name, string Slug);
