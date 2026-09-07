using FlatpackApp.Api.Security;
using FlatpackApp.Application.Identity;
using FlatpackApp.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController(IAntiforgery antiforgery, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IWorkspaceContextCookie workspaceCookie, IPlatformAccessDirectory platformAccess) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("antiforgery", Name = "Authentication_Antiforgery")]
    public ActionResult<AntiforgeryResponse> Antiforgery()
    {
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryResponse(tokens.RequestToken ?? string.Empty));
    }

    [AllowAnonymous]
    [HttpPost("login", Name = "Authentication_Login")]
    public async Task<ActionResult<SessionResponse>> Login(LoginRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        workspaceCookie.Clear(HttpContext);
        ApplicationUser? user = await users.FindByEmailAsync(request.Email);
        if (user is null || !user.EmailConfirmed)
        {
            return Problem(statusCode: 401, title: "Invalid credentials");
        }

        Microsoft.AspNetCore.Identity.SignInResult password = await signIn.PasswordSignInAsync(
            user,
            request.Password,
            request.RememberMe,
            lockoutOnFailure: true);
        if (password.RequiresTwoFactor)
        {
            return Problem(statusCode: 428, title: "Multi-factor authentication required");
        }

        if (!password.Succeeded)
        {
            return Problem(statusCode: password.IsLockedOut ? 423 : 401, title: password.IsLockedOut ? "Account locked" : "Invalid credentials");
        }

        user.LastSignedInAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);
        return Ok(await ToResponse(user));
    }

    [AllowAnonymous]
    [HttpPost("login/mfa", Name = "Authentication_LoginMfa")]
    public async Task<ActionResult<SessionResponse>> LoginMfa(MfaLoginRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        ApplicationUser? user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return Problem(statusCode: 401, title: "The multi-factor sign-in has expired");
        }

        Microsoft.AspNetCore.Identity.SignInResult result = request.IsRecoveryCode
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(request.Code.Trim())
            : await signIn.TwoFactorAuthenticatorSignInAsync(Normalize(request.Code), request.RememberMe, request.RememberClient);

        if (!result.Succeeded)
        {
            return Problem(
                statusCode: result.IsLockedOut ? 423 : 401,
                title: result.IsLockedOut ? "Account locked" : "Invalid multi-factor code");
        }

        user.LastSignedInAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);
        return Ok(await ToResponse(user));
    }

    [Authorize(AuthenticationSchemes = FlatpackAuthenticationSchemes.ApplicationCookie)]
    [HttpGet("session", Name = "Authentication_Session")]
    public async Task<ActionResult<SessionResponse>> Session()
    {
        ApplicationUser? user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(await ToResponse(user));
    }

    [Authorize(AuthenticationSchemes = FlatpackAuthenticationSchemes.ApplicationCookie)]
    [HttpPost("logout", Name = "Authentication_Logout")]
    public async Task<IActionResult> Logout()
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        await signIn.SignOutAsync();
        workspaceCookie.Clear(HttpContext);
        return NoContent();
    }

    private async Task<SessionResponse> ToResponse(ApplicationUser user)
    {
        IList<string> assignedRoles = await users.GetRolesAsync(user);
        PlatformRoleDefinition? role = null;
        foreach (string roleKey in assignedRoles)
        {
            role = await platformAccess.FindRoleAsync(roleKey);
            if (role is not null) break;
        }
        if (role is null && user.IsPlatformAdministrator) role = PlatformRoles.Find(PlatformRoles.Administrator);
        bool hasPlatformAccess = role is not null && !user.IsPlatformAccessSuspended;
        return new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            role?.Key == PlatformRoles.Administrator,
            hasPlatformAccess,
            hasPlatformAccess ? role?.Key : null,
            hasPlatformAccess ? role!.Permissions.Order(StringComparer.Ordinal).ToArray() : []);
    }

    private static string Normalize(string code) =>
        code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
}

public sealed record LoginRequest(string Email, string Password, bool RememberMe);
public sealed record MfaLoginRequest(string Code, bool IsRecoveryCode, bool RememberMe, bool RememberClient);
public sealed record SessionResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    bool IsPlatformAdministrator,
    bool HasPlatformAccess,
    string? PlatformRole,
    IReadOnlyList<string> PlatformPermissions);
public sealed record AntiforgeryResponse(string Token);
