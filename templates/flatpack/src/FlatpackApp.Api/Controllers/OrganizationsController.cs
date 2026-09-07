using FlatpackApp.Application.Common;
using FlatpackApp.Application.Identity;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Api.Security;
using FlatpackApp.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[RequirePlatformPermission(PlatformPermissions.TenantsRead)]
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
        CreateOrganizationCommand command = new(request.Name, request.Slug, request.AdministratorEmail);
        Result<CreateOrganizationResult> result = await createOrganization.HandleAsync(command, cancellationToken);
        return !result.IsSuccess || result.Value is null
            ? Problem(statusCode: result.ErrorCode == "slug_conflict" ? 409 : 400, title: result.ErrorCode, detail: result.ErrorMessage)
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
}

public sealed record CreateOrganizationRequest(string Name, string Slug, string AdministratorEmail);
public sealed record UpdateOrganizationRequest(string Name);
