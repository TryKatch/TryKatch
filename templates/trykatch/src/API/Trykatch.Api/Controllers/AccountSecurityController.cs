using Trykatch.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
[Route("api/v1/account/security")]
public sealed class AccountSecurityController(UserManager<ApplicationUser> users, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost("mfa/setup", Name = "AccountSecurity_SetupMfa")]
    public async Task<ActionResult<MfaSetupResponse>> SetupMfa()
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        ApplicationUser user = await GetUserAsync();
        string? key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        string email = user.Email ?? user.UserName ?? user.Id.ToString();
        string uri = $"otpauth://totp/{Uri.EscapeDataString("Trykatch:" + email)}?secret={key}&issuer=Trykatch&digits=6";
        return Ok(new MfaSetupResponse(key ?? string.Empty, uri));
    }

    [HttpPost("mfa/enable", Name = "AccountSecurity_EnableMfa")]
    public async Task<ActionResult<RecoveryCodesResponse>> EnableMfa(MfaCodeRequest request)
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        ApplicationUser user = await GetUserAsync();
        bool valid = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Normalize(request.Code));
        if (!valid) return Problem(statusCode: 400, title: "Invalid authenticator code");
        await users.SetTwoFactorEnabledAsync(user, true);
        await users.UpdateSecurityStampAsync(user);
        IEnumerable<string>? recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return Ok(new RecoveryCodesResponse(recoveryCodes?.ToArray() ?? []));
    }

    [HttpPost("mfa/recovery-codes", Name = "AccountSecurity_RegenerateRecoveryCodes")]
    public async Task<ActionResult<RecoveryCodesResponse>> RegenerateRecoveryCodes()
    {
        await antiforgery.ValidateRequestAsync(HttpContext);
        ApplicationUser user = await GetUserAsync();
        if (!await users.GetTwoFactorEnabledAsync(user)) return Problem(statusCode: 409, title: "MFA is not enabled");
        IEnumerable<string>? recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await users.UpdateSecurityStampAsync(user);
        return Ok(new RecoveryCodesResponse(recoveryCodes?.ToArray() ?? []));
    }

    private async Task<ApplicationUser> GetUserAsync() =>
        await users.GetUserAsync(User) ?? throw new InvalidOperationException("Authenticated user no longer exists.");

    private static string Normalize(string code) => code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
}

public sealed record MfaCodeRequest(string Code);
public sealed record MfaSetupResponse(string SharedKey, string AuthenticatorUri);
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);
