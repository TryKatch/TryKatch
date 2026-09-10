using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Api.Security;
using Trykatch.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
[Route("api/v1/invitations")]
public sealed class InvitationsController(OrganizationAdministration administration, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IWorkspaceContextCookie workspaceCookie, IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("preview/{token}", Name = "Invitations_Preview")]
    public async Task<ActionResult<InvitationPreviewResponse>> Preview(string token, CancellationToken cancellationToken)
    {
        Result<InvitationPreviewDto> result = await administration.PreviewInvitationAsync(token, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
            return Problem(statusCode: 400, title: result.ErrorCode, detail: result.ErrorMessage);
        ApplicationUser? existing = await users.FindByEmailAsync(result.Value.Email);
        return Ok(new InvitationPreviewResponse(result.Value.Email, result.Value.OrganizationName, result.Value.ExpiresAt, existing is not null));
    }

    [AllowAnonymous]
    [HttpPost("activate", Name = "Invitations_Activate")]
    [CookieAntiforgery]
    public async Task<ActionResult<AcceptInvitationResponse>> Activate(ActivateOrganizationInvitationRequest request, CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        Result<InvitationPreviewDto> preview = await administration.PreviewInvitationAsync(request.Token, cancellationToken);
        if (!preview.IsSuccess || preview.Value is null)
            return Problem(statusCode: 400, title: preview.ErrorCode, detail: preview.ErrorMessage);
        if (await users.FindByEmailAsync(preview.Value.Email) is not null)
            return Problem(statusCode: 409, title: "account_exists", detail: "Sign in with the invited email address to accept this invitation.");
        string firstName = request.FirstName?.Trim() ?? string.Empty;
        string lastName = request.LastName?.Trim() ?? string.Empty;
        if (firstName.Length is < 1 or > 60 || lastName.Length is < 1 or > 60)
            return Problem(statusCode: 400, title: "validation", detail: "First name and last name are required and must be 60 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.Password))
            return Problem(statusCode: 400, title: "validation", detail: "A password is required.");
        string displayName = $"{firstName} {lastName}";

        ApplicationUser user = new()
        {
            Id = Guid.CreateVersion7(),
            Email = preview.Value.Email,
            UserName = preview.Value.Email,
            DisplayName = displayName,
            EmailConfirmed = true,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        IdentityResult created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            return Problem(statusCode: 400, title: "identity_validation", detail: string.Join(" ", created.Errors.Select(error => error.Description)));

        Result<Guid> accepted;
        try
        {
            accepted = await administration.AcceptInvitationAsync(request.Token, user.Id, preview.Value.Email, cancellationToken);
        }
        catch
        {
            await users.DeleteAsync(user);
            throw;
        }
        if (!accepted.IsSuccess)
        {
            await users.DeleteAsync(user);
            return Problem(statusCode: 400, title: accepted.ErrorCode, detail: accepted.ErrorMessage);
        }

        await signIn.SignInAsync(user, isPersistent: true);
        workspaceCookie.Write(HttpContext, accepted.Value, persistent: true);
        return Ok(new AcceptInvitationResponse(accepted.Value));
    }

    [HttpPost("accept", Name = "Invitations_Accept")]
    [CookieAntiforgery]
    public async Task<ActionResult<AcceptInvitationResponse>> Accept(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await users.GetUserAsync(User);
        if (user?.Email is null || !user.EmailConfirmed) return Unauthorized();
        Result<Guid> result = await administration.AcceptInvitationAsync(request.Token, user.Id, user.Email, cancellationToken);
        if (!result.IsSuccess)
            return Problem(statusCode: result.ErrorCode == "conflict" ? 409 : 400, title: result.ErrorCode, detail: result.ErrorMessage);

        workspaceCookie.Write(HttpContext, result.Value, persistent: true);
        return Ok(new AcceptInvitationResponse(result.Value));
    }
}

public sealed record AcceptInvitationRequest(string Token);
public sealed record AcceptInvitationResponse(Guid OrganizationId);
public sealed record InvitationPreviewResponse(string Email, string OrganizationName, DateTimeOffset ExpiresAt, bool AccountExists);
public sealed record ActivateOrganizationInvitationRequest(string Token, string? FirstName, string? LastName, string? Password);
