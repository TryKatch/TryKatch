using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Trykatch.Api.Security;
using Trykatch.Application.Quotas;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize(Policy = "OrganizationUsageRead")]
[OrganizationScoped]
[Route("api/v1/quotas")]
public sealed class QuotasController(IOrganizationQuotaUsageReader usage) : ControllerBase
{
    [HttpGet("usage", Name = "Quotas_CurrentUsage")]
    public async Task<ActionResult<OrganizationQuotaUsage>> CurrentUsage(CancellationToken cancellationToken)
    {
        var result = await usage.GetAsync(cancellationToken);
        return result.IsSuccess ? Ok(result.Value!) : Problem(statusCode: result.ErrorCode == "forbidden" ? 403 : 503,
            title: result.ErrorCode, detail: result.ErrorMessage);
    }
}
