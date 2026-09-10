using System.Text;
using Trykatch.Api.Security;
using Trykatch.Application.Identity;
using Trykatch.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace Trykatch.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController(
    IAntiforgery antiforgery,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IWorkspaceContextCookie workspaceCookie,
    IPlatformAccessDirectory platformAccess,
    IAccountRecoveryNotifier recoveryNotifier,
    IApplicationUrlResolver applicationUrls,
    IWebHostEnvironment environment,
    ILogger<AuthenticationController> logger) : ControllerBase
{
    private static readonly Action<ILogger, Guid, Exception?> LogPasswordResetDeliveryFailure =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1001, nameof(ForgotPassword)),
            "Password reset delivery failed for account {UserId}");

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

    [Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
    [HttpGet("session", Name = "Authentication_Session")]
    public async Task<ActionResult<SessionResponse>> Session()
    {
        ApplicationUser? user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(await ToResponse(user));
    }

    [Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
    [HttpPost("logout", Name = "Authentication_Logout")]
    public async Task<IActionResult> Logout()
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        await signIn.SignOutAsync();
        workspaceCookie.Clear(HttpContext);
        return NoContent();
    }

    [AllowAnonymous]
    [EnableRateLimiting("account-recovery")]
    [HttpPost("password/forgot", Name = "Authentication_ForgotPassword")]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword(
        ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        string email = request.Email.Trim();
        string? developmentResetUrl = null;
        ApplicationUser? user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);
        if (user is not null && user.EmailConfirmed)
        {
            string token = await users.GeneratePasswordResetTokenAsync(user);
            string encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            string resetUrl = $"{applicationUrls.ResolveBaseUrl(Request)}/reset-password?email={Uri.EscapeDataString(user.Email ?? email)}&token={Uri.EscapeDataString(encodedToken)}";
            if (environment.IsDevelopment())
            {
                developmentResetUrl = resetUrl;
            }

            try
            {
                await recoveryNotifier.SendPasswordResetAsync(
                    user.Email ?? email,
                    user.DisplayName,
                    resetUrl,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                LogPasswordResetDeliveryFailure(logger, user.Id, exception);
            }
        }

        return Accepted(new ForgotPasswordResponse(
            "If an eligible account exists, password reset instructions are on the way.",
            recoveryNotifier.IsConfigured,
            developmentResetUrl));
    }

    [AllowAnonymous]
    [EnableRateLimiting("account-recovery")]
    [HttpPost("password/reset", Name = "Authentication_ResetPassword")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        if (request.NewPassword != request.ConfirmPassword)
        {
            return Problem(statusCode: 400, title: "validation", detail: "The new passwords do not match.");
        }

        ApplicationUser? user = await users.FindByEmailAsync(request.Email.Trim());
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return InvalidReset();
        }

        if (user is null)
        {
            return InvalidReset();
        }

        IdentityResult result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            bool passwordValidationFailed = result.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal));
            return passwordValidationFailed
                ? Problem(statusCode: 400, title: "password_validation", detail: string.Join(" ", result.Errors.Select(error => error.Description)))
                : InvalidReset();
        }

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

    private ObjectResult InvalidReset() =>
        Problem(statusCode: 400, title: "invalid_reset", detail: "This password reset link is invalid or has expired.");
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
public sealed record ForgotPasswordRequest(string Email);
public sealed record ForgotPasswordResponse(string Message, bool DeliveryConfigured, string? DevelopmentResetUrl);
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword, string ConfirmPassword);
