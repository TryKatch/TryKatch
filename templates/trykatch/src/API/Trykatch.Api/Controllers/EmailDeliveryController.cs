using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;

namespace Trykatch.Api.Controllers;

[ApiController]
[PlatformDataScoped]
[Route("api/v1/platform/email-deliveries")]
public sealed class EmailDeliveryController(EmailDeliveryAdministration administration) : ControllerBase
{
    [HttpGet(Name = "EmailDelivery_List")]
    [RequirePlatformPermission(PlatformPermissions.EmailRead)]
    [ProducesResponseType<PagedResult<EmailDeliveryView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<EmailDeliveryView>>> List(string boundary = "organization", int page = 1,
        int pageSize = 25, string? state = null, Guid? organizationId = null, Guid? deliveryId = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        return Respond(await administration.ListAsync(actorId, boundary, page, pageSize, state, organizationId, deliveryId, cancellationToken));
    }

    [HttpGet("templates", Name = "EmailTemplates_List")]
    [RequirePlatformPermission(PlatformPermissions.EmailRead)]
    [ProducesResponseType<PagedResult<EmailTemplateView>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<EmailTemplateView>>> Templates(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        return Respond(await administration.TemplatesAsync(actorId, page, pageSize, cancellationToken));
    }

    [HttpGet("templates/preview", Name = "EmailTemplates_Preview")]
    [RequirePlatformPermission(PlatformPermissions.EmailRead)]
    [ProducesResponseType<EmailTemplatePreview>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EmailTemplatePreview>> Preview(string templateKey, int templateVersion = 1, CancellationToken cancellationToken = default)
    {
        if (!TryGetActor(out Guid actorId)) return Unauthorized();
        return Respond(await administration.PreviewAsync(actorId, templateKey, templateVersion, cancellationToken));
    }

    private ActionResult<T> Respond<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.ErrorCode switch
        { "forbidden" => 403, "not_found" => 404, "unavailable" => 503, _ => 400 }, title: result.ErrorCode);
    private bool TryGetActor(out Guid actorId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out actorId);
}
