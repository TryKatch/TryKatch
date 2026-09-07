using FlatpackApp.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = FlatpackAuthenticationSchemes.ApplicationCookie)]
[Route("api/v1/account")]
public sealed class AccountProfileController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet(Name = "AccountProfile_Get")]
    public async Task<ActionResult<AccountProfileResponse>> Get()
    {
        ApplicationUser user = await GetUserAsync();
        return Ok(ToResponse(user));
    }

    [HttpPut(Name = "AccountProfile_Update")]
    public async Task<ActionResult<AccountProfileResponse>> Update(UpdateAccountProfileRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        string displayName = request.DisplayName.Trim();
        if (displayName.Length is < 2 or > 120)
        {
            return Problem(statusCode: 400, title: "Invalid display name", detail: "Display name must be between 2 and 120 characters.");
        }

        ApplicationUser user = await GetUserAsync();
        user.DisplayName = displayName;
        IdentityResult result = await users.UpdateAsync(user);
        return result.Succeeded ? Ok(ToResponse(user)) : IdentityProblem(result);
    }

    [HttpPost("password", Name = "AccountProfile_ChangePassword")]
    public async Task<IActionResult> ChangePassword(ChangeAccountPasswordRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        ApplicationUser user = await GetUserAsync();
        IdentityResult result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded) return IdentityProblem(result);

        await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        return NoContent();
    }

    private async Task<ApplicationUser> GetUserAsync() =>
        await users.GetUserAsync(User) ?? throw new InvalidOperationException("Authenticated user no longer exists.");

    private ObjectResult IdentityProblem(IdentityResult result) =>
        Problem(
            statusCode: 400,
            title: "Account update failed",
            detail: string.Join(" ", result.Errors.Select(error => error.Description)));

    private static AccountProfileResponse ToResponse(ApplicationUser user) =>
        new(user.Id, user.DisplayName, user.Email ?? string.Empty, user.EmailConfirmed, user.TwoFactorEnabled);
}

public sealed record UpdateAccountProfileRequest(string DisplayName);
public sealed record ChangeAccountPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record AccountProfileResponse(Guid UserId, string DisplayName, string Email, bool EmailConfirmed, bool TwoFactorEnabled);
