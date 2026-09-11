using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;

namespace Trykatch.Api.Controllers;

[ApiController]
[RequirePlatformPermission(PlatformPermissions.TenantsRead)]
[PlatformDataScoped]
[Route("api/v1/tenants")]
public sealed class OrganizationsController(
    IOrganizationDirectory organizations,
    CreateOrganization createOrganization,
    ManageOrganizations manageOrganizations) : ControllerBase
{
    [HttpGet(Name = "Organizations_List")]
    public async Task<ActionResult<PagedResult<OrganizationDto>>> List(int page = 1, int pageSize = 25, string? search = null, CancellationToken cancellationToken = default)
    {
        PagedResult<Organization> result = await organizations.ListAsync(Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search, cancellationToken);
        return Ok(new PagedResult<OrganizationDto>(result.Items.Select(x => new OrganizationDto(x.Id, x.Name, x.Slug, x.IsActive, x.CreatedAt)).ToArray(), result.Page, result.PageSize, result.TotalCount));
    }

    [HttpGet("{organizationId:guid}", Name = "Organizations_Get")]
    public async Task<ActionResult<OrganizationDto>> Get(Guid organizationId, CancellationToken cancellationToken)
    {
        Result<OrganizationDto> result = await manageOrganizations.GetAsync(organizationId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost(Name = "Organizations_Create")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.TenantsManage)]
    public async Task<ActionResult<CreateOrganizationResult>> Create(CreateOrganizationRequest request, CancellationToken cancellationToken)
    {
        string? subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out Guid actorId))
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authenticated subject is invalid");
        if (!TryParsePlacement(request.Placement, out OrganizationDataPlacementKind placement))
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "unsupported_tenant_placement",
                detail: "Only shared PostgreSQL placement is available in this release. No tenant was created.");
        }

        CreateOrganizationCommand command = new(
            request.Name,
            request.Slug,
            request.AdministratorEmail,
            actorId,
            placement);
        Result<CreateOrganizationResult> result = await createOrganization.HandleAsync(command, cancellationToken);
        return !result.IsSuccess || result.Value is null
            ? Problem(
                statusCode: result.ErrorCode switch
                {
                    "slug_conflict" => StatusCodes.Status409Conflict,
                    "unsupported_tenant_placement" => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status400BadRequest
                },
                title: result.ErrorCode,
                detail: result.ErrorMessage)
            : CreatedAtAction(nameof(Get), new { organizationId = result.Value.Organization.Id }, result.Value);
    }

    [HttpPut("{organizationId:guid}", Name = "Organizations_Update")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.TenantsManage)]
    public async Task<ActionResult<OrganizationDto>> Update(Guid organizationId, UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        Result<OrganizationDto> result = await manageOrganizations.UpdateAsync(new UpdateOrganizationCommand(organizationId, request.Name), cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{organizationId:guid}/deactivate", Name = "Organizations_Deactivate")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.TenantsManage)]
    public async Task<ActionResult<OrganizationDto>> Deactivate(Guid organizationId, CancellationToken cancellationToken)
    {
        Result<OrganizationDto> result = await manageOrganizations.SetStatusAsync(organizationId, false, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{organizationId:guid}/reactivate", Name = "Organizations_Reactivate")]
    [CookieAntiforgery]
    [RequirePlatformPermission(PlatformPermissions.TenantsManage)]
    public async Task<ActionResult<OrganizationDto>> Reactivate(Guid organizationId, CancellationToken cancellationToken)
    {
        Result<OrganizationDto> result = await manageOrganizations.SetStatusAsync(organizationId, true, cancellationToken);
        return ToActionResult(result);
    }

    private ActionResult<OrganizationDto> ToActionResult(Result<OrganizationDto> result) =>
        result.IsSuccess && result.Value is not null
            ? Ok(result.Value)
            : Problem(
                statusCode: result.ErrorCode == "not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                title: result.ErrorCode,
                detail: result.ErrorMessage);

    private static bool TryParsePlacement(
        string? value,
        out OrganizationDataPlacementKind placement)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "shared", StringComparison.OrdinalIgnoreCase))
        {
            placement = OrganizationDataPlacementKind.Shared;
            return true;
        }

        placement = default;
        return false;
    }
}

public sealed record CreateOrganizationRequest(
    string Name,
    string Slug,
    string AdministratorEmail,
    string? Placement = null);
public sealed record UpdateOrganizationRequest(string Name);
