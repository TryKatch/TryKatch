using TrykatchApp.Api.Security;
using TrykatchApp.Application.Authorization;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Application.Overview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TrykatchApp.Api.Controllers;

[ApiController]
[Authorize]
[OrganizationScoped]
[Route("api/v1/workspace/overview")]
public sealed class WorkspaceOverviewController(
    IWorkspaceOverviewReader overview,
    IOrganizationContext organization) : ControllerBase
{
    [HttpGet(Name = "WorkspaceOverview_Get")]
    [RequirePermission(Permissions.OrganizationsRead)]
    public async Task<ActionResult<WorkspaceOverview>> Get(CancellationToken cancellationToken) =>
        Ok(await overview.GetAsync(organization.OrganizationId, cancellationToken));
}
