using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Outbox;

namespace Trykatch.Api.Controllers;

[ApiController]
[PlatformDataScoped]
[Route("api/v1/platform/outbox")]
public sealed class OutboxRecoveryController(IOutboxRecoveryDirectory directory) : ControllerBase
{
    [HttpGet("failures", Name = "Outbox_ListFailures")]
    [RequirePlatformPermission(PlatformPermissions.OutboxRead)]
    public async Task<ActionResult<PagedResult<OutboxFailure>>> ListFailures(
        int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        Result<PagedResult<OutboxFailure>> result = await directory.ListFailuresAsync(actorId, page, pageSize, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: StatusCodes.Status403Forbidden, title: result.ErrorCode);
    }

    [HttpPost("{messageId:guid}/replay", Name = "Outbox_RequestReplay")]
    [ProducesResponseType<OutboxReplayAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [CookieAntiforgery]
    [EnableRateLimiting("outbox-replay")]
    [RequirePlatformPermission(PlatformPermissions.OutboxReplay)]
    public async Task<ActionResult<OutboxReplayAccepted>> RequestReplay(
        Guid messageId, OutboxReplayRequestBody request, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        Result<OutboxReplayAccepted> result = await directory.RequestReplayAsync(actorId,
            new RequestOutboxReplay(request.RequestId, messageId, request.ExpectedFailedGeneration), cancellationToken);
        if (result.IsSuccess) return AcceptedAtAction(nameof(GetReplayOutcome), new { requestId = request.RequestId }, result.Value);
        return Problem(statusCode: result.ErrorCode switch
        {
            "request_conflict" => StatusCodes.Status409Conflict,
            "forbidden" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        }, title: result.ErrorCode, detail: result.ErrorMessage);
    }

    [HttpGet("replay-requests/{requestId:guid}", Name = "Outbox_GetReplayOutcome")]
    [ProducesResponseType<OutboxReplayOutcome>(StatusCodes.Status200OK)]
    [ProducesResponseType<OutboxReplayPending>(StatusCodes.Status202Accepted)]
    [RequirePlatformPermission(PlatformPermissions.OutboxRead)]
    public async Task<ActionResult<OutboxReplayOutcome>> GetReplayOutcome(Guid requestId, CancellationToken cancellationToken)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        Result<OutboxReplayOutcome> result = await directory.GetReplayOutcomeAsync(actorId, requestId, cancellationToken);
        if (result.IsSuccess) return Ok(result.Value);
        return result.ErrorCode == "pending"
            ? Accepted(new OutboxReplayPending(requestId, "pending"))
            : Problem(statusCode: StatusCodes.Status403Forbidden, title: result.ErrorCode);
    }

    private bool TryGetActor(out Guid actorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out actorId);
}

public sealed record OutboxReplayRequestBody(Guid RequestId, int ExpectedFailedGeneration);
