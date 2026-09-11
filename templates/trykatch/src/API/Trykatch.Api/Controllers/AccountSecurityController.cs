using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Trykatch.Api.Security;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Identity;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = Trykatch.Identity.AuthenticationSchemes.ApplicationCookie)]
[EnableRateLimiting("account-security")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/account/security")]
public sealed class AccountSecurityController(IAccountSecurity security, SignInManager<ApplicationUser> signIn, IOptions<IdentityOptions> identityOptions) : ControllerBase
{
    [HttpPost("reauthenticate", Name = "AccountSecurity_Reauthenticate")]
    [CookieAntiforgery]
    public async Task<ActionResult<RecentAssuranceGrant>> Reauthenticate(ReauthenticationRequest request, CancellationToken cancellationToken) =>
        Respond(await security.ReauthenticateAsync(await GetContextAsync(), new(request.Purpose, request.Password, request.Code, request.IsRecoveryCode), cancellationToken));

    [HttpPost("mfa/setup", Name = "AccountSecurity_SetupMfa")]
    [CookieAntiforgery]
    public async Task<ActionResult<PendingMfaSetup>> SetupMfa(MfaGrantRequest request, CancellationToken cancellationToken) =>
        Respond(await security.BeginEnrollmentAsync(await GetContextAsync(), request.Grant, cancellationToken));

    [HttpPost("mfa/enable", Name = "AccountSecurity_EnableMfa")]
    [CookieAntiforgery]
    public async Task<ActionResult<MfaRecoveryCodes>> EnableMfa(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        Result<MfaRecoveryCodes> result = await security.ConfirmEnrollmentAsync(await GetContextAsync(), request.EnrollmentId, request.Code, cancellationToken);
        if (result.IsSuccess) await signIn.SignOutAsync();
        return Respond(result);
    }

    [HttpPost("mfa/cancel", Name = "AccountSecurity_CancelMfaEnrollment")]
    [CookieAntiforgery]
    public async Task<IActionResult> CancelMfaEnrollment(MfaEnrollmentRequest request, CancellationToken cancellationToken)
    {
        Result<bool> result = await security.CancelEnrollmentAsync(await GetContextAsync(), request.EnrollmentId, cancellationToken);
        return result.IsSuccess ? NoContent() : ToProblem(result);
    }

    [HttpPost("mfa/recovery-codes", Name = "AccountSecurity_RegenerateRecoveryCodes")]
    [CookieAntiforgery]
    public async Task<ActionResult<MfaRecoveryCodes>> RegenerateRecoveryCodes(MfaGrantRequest request, CancellationToken cancellationToken)
    {
        Result<MfaRecoveryCodes> result = await security.RegenerateRecoveryCodesAsync(await GetContextAsync(), request.Grant, cancellationToken);
        if (result.IsSuccess) await signIn.SignOutAsync();
        return Respond(result);
    }

    [HttpPost("mfa/disable", Name = "AccountSecurity_DisableMfa")]
    [CookieAntiforgery]
    public async Task<IActionResult> DisableMfa(MfaGrantRequest request, CancellationToken cancellationToken)
    {
        Result<bool> result = await security.DisableAsync(await GetContextAsync(), request.Grant, cancellationToken);
        if (!result.IsSuccess) return ToProblem(result);
        await signIn.SignOutAsync();
        return NoContent();
    }

    private async Task<AccountSecurityContext> GetContextAsync()
    {
        AuthenticateResult authentication = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        string session = authentication.Properties?.Items.TryGetValue(AccountSecuritySession.PropertyName, out string? sessionId) == true ? sessionId ?? "" : "";
        return new(Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid userId) ? userId : Guid.Empty,
            session, User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType) ?? "");
    }

    private ActionResult<T> Respond<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : ToProblem(result);
    private ObjectResult ToProblem<T>(Result<T> result) => Problem(statusCode: result.ErrorCode switch
    {
        "enrollment_conflict" => StatusCodes.Status409Conflict,
        "reauthentication_unsupported" => StatusCodes.Status422UnprocessableEntity,
        "identity_validation" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status403Forbidden
    }, title: result.ErrorCode, detail: result.ErrorMessage);
}

public sealed record ReauthenticationRequest([Required, MaxLength(64)] string Purpose, [Required, MaxLength(1024)] string Password, [MaxLength(128)] string? Code = null, bool IsRecoveryCode = false);
public sealed record MfaGrantRequest([MaxLength(128)] string? Grant = null);
public sealed record MfaCodeRequest(Guid EnrollmentId, [Required, MaxLength(32)] string Code);
public sealed record MfaEnrollmentRequest(Guid EnrollmentId);
