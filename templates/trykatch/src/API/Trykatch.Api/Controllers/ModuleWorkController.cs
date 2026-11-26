using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Jobs;

namespace Trykatch.Api.Controllers;

[ApiController]
[PlatformDataScoped]
[Route("api/v1/platform/module-work")]
public sealed class ModuleWorkController(IModuleWorkAdministration administration) : ControllerBase
{
    [HttpGet(Name = "ModuleWork_List")]
    [RequirePlatformPermission(PlatformPermissions.JobsRead)]
    [ProducesResponseType<PagedResult<ModuleWorkView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ModuleWorkView>>> List(int page = 1, int pageSize = 25,
        string? state = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        Result<PagedResult<ModuleWorkView>> result = await administration.ListAsync(actorId, page, pageSize, state, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.ErrorCode == "forbidden" ? 403 : 400, title: result.ErrorCode);
    }

    [HttpPost("{workId:guid}/pause", Name = "ModuleWork_Pause")]
    [RequirePlatformPermission(PlatformPermissions.JobsManage)]
    [CookieAntiforgery]
    [EnableRateLimiting("outbox-replay")]
    [ProducesResponseType<ModuleWorkActionReceipt>(StatusCodes.Status200OK)]
    public Task<ActionResult<ModuleWorkActionReceipt>> Pause(Guid workId, ModuleWorkActionRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(workId, "pause", request, cancellationToken);

    [HttpPost("{workId:guid}/resume", Name = "ModuleWork_Resume")]
    [RequirePlatformPermission(PlatformPermissions.JobsManage)]
    [CookieAntiforgery]
    [EnableRateLimiting("outbox-replay")]
    [ProducesResponseType<ModuleWorkActionReceipt>(StatusCodes.Status200OK)]
    public Task<ActionResult<ModuleWorkActionReceipt>> Resume(Guid workId, ModuleWorkActionRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(workId, "resume", request, cancellationToken);

    [HttpPost("{workId:guid}/retry", Name = "ModuleWork_Retry")]
    [RequirePlatformPermission(PlatformPermissions.JobsManage)]
    [CookieAntiforgery]
    [EnableRateLimiting("outbox-replay")]
    [ProducesResponseType<ModuleWorkActionReceipt>(StatusCodes.Status200OK)]
    public Task<ActionResult<ModuleWorkActionReceipt>> Retry(Guid workId, ModuleWorkActionRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(workId, "retry", request, cancellationToken);

    private async Task<ActionResult<ModuleWorkActionReceipt>> ChangeAsync(Guid workId, string action,
        ModuleWorkActionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        Result<ModuleWorkActionReceipt> result = await administration.ChangeAsync(actorId, workId, action, request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.ErrorCode switch
        {
            "forbidden" => 403, "not_found" => 404, "request_conflict" or "state_conflict" => 409, _ => 400
        }, title: result.ErrorCode);
    }

    private bool TryGetActor(out Guid actorId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out actorId);
}
