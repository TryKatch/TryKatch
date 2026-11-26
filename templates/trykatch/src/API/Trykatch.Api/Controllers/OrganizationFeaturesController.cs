using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Trykatch.Api.Security;
using Trykatch.Application.Features;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize]
[OrganizationScoped]
[Route("api/v1/features")]
public sealed class OrganizationFeaturesController(IOrganizationFeatureSnapshotReader reader) : ControllerBase
{
    [HttpGet(Name = "Features_Current")]
    public async Task<ActionResult<OrganizationFeatureSnapshot>> Current(CancellationToken cancellationToken) =>
        Ok(await reader.ReadAsync(cancellationToken));
}
