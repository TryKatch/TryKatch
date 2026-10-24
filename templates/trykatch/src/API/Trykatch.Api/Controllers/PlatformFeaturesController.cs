using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Features;
using Trykatch.Application.Identity;

namespace Trykatch.Api.Controllers;

[ApiController]
[PlatformDataScoped]
[Route("api/v1/platform/features")]
public sealed class PlatformFeaturesController(FeatureAdministration administration) : ControllerBase
{
    [HttpGet(Name = "PlatformFeatures_List")]
    [RequirePlatformPermission(PlatformPermissions.FeaturesRead)]
    public async Task<ActionResult<FeatureAdministrationSnapshot>> List(Guid? organizationId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actor)) return Unauthorized();
        return Respond(await administration.ListAsync(actor, organizationId, cancellationToken));
    }

    [HttpGet("{featureKey}/history", Name = "PlatformFeatures_History")]
    [RequirePlatformPermission(PlatformPermissions.FeaturesRead)]
    public async Task<ActionResult<PagedResult<FeatureControlHistoryEntry>>> History(string featureKey,
        Guid? organizationId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actor)) return Unauthorized();
        return Respond(await administration.HistoryAsync(actor, new(featureKey, organizationId), page, pageSize, cancellationToken));
    }

    [HttpPut("{featureKey}/environment", Name = "PlatformFeatures_SaveEnvironment")]
    [RequirePlatformPermission(PlatformPermissions.FeaturesManage)]
    [CookieAntiforgery]
    public async Task<ActionResult<FeatureControlState>> SaveEnvironment(string featureKey,
        SaveEnvironmentFeatureControl command, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actor)) return Unauthorized();
        return Respond(await administration.SaveEnvironmentAsync(actor, featureKey, command, cancellationToken));
    }

    [HttpPut("{featureKey}/organizations/{organizationId:guid}", Name = "PlatformFeatures_SaveOrganization")]
    [RequirePlatformPermission(PlatformPermissions.FeaturesManage)]
    [CookieAntiforgery]
    public async Task<ActionResult<FeatureControlState>> SaveOrganization(string featureKey, Guid organizationId,
        SaveOrganizationFeatureOverride command, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actor)) return Unauthorized();
        return Respond(await administration.SaveOrganizationAsync(actor, new(featureKey, organizationId), command, cancellationToken));
    }

    private ActionResult<T> Respond<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(
        statusCode: result.ErrorCode switch
        {
            "forbidden" => StatusCodes.Status403Forbidden,
            "not_found" => StatusCodes.Status404NotFound,
            "feature_control_busy" or "feature_control_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        }, title: result.ErrorCode, detail: result.ErrorMessage,
        extensions: new Dictionary<string, object?> { ["code"] = result.ErrorCode });

    private bool TryGetActor(out Guid actor) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out actor) && actor != Guid.Empty;
}
