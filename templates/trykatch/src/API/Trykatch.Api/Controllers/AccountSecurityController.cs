using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
public sealed class AccountSecurityController(IAccountSecurity security, SignInManager<ApplicationUser> signIn, ICurrentAccountSession currentSession) : ControllerBase
{
    [HttpPost("reauthenticate", Name = "AccountSecurity_Reauthenticate")]
    [CookieAntiforgery]
    public async Task<ActionResult<RecentAssuranceGrant>> Reauthenticate(ReauthenticationRequest request, CancellationToken cancellationToken) =>
        Respond(await security.ReauthenticateAsync(GetContext(), new(request.Purpose, request.Password, request.Code, request.IsRecoveryCode), cancellationToken));

    [HttpPost("mfa/setup", Name = "AccountSecurity_SetupMfa")]
    [CookieAntiforgery]
    public async Task<ActionResult<PendingMfaSetup>> SetupMfa(MfaGrantRequest request, CancellationToken cancellationToken) =>
        Respond(await security.BeginEnrollmentAsync(GetContext(), request.Grant, cancellationToken));

    [HttpPost("mfa/enable", Name = "AccountSecurity_EnableMfa")]
    [CookieAntiforgery]
    public async Task<ActionResult<MfaRecoveryCodes>> EnableMfa(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        Result<MfaRecoveryCodes> result = await security.ConfirmEnrollmentAsync(GetContext(), request.EnrollmentId, request.Code, cancellationToken);
        if (result.IsSuccess) await signIn.SignOutAsync();
        return Respond(result);
    }

    [HttpPost("mfa/cancel", Name = "AccountSecurity_CancelMfaEnrollment")]
    [CookieAntiforgery]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CancelMfaEnrollment(MfaEnrollmentRequest request, CancellationToken cancellationToken)
    {
        Result<bool> result = await security.CancelEnrollmentAsync(GetContext(), request.EnrollmentId, cancellationToken);
        return result.IsSuccess ? NoContent() : ToProblem(result);
    }

    [HttpPost("mfa/recovery-codes", Name = "AccountSecurity_RegenerateRecoveryCodes")]
    [CookieAntiforgery]
    public async Task<ActionResult<MfaRecoveryCodes>> RegenerateRecoveryCodes(MfaGrantRequest request, CancellationToken cancellationToken)
    {
        Result<MfaRecoveryCodes> result = await security.RegenerateRecoveryCodesAsync(GetContext(), request.Grant, cancellationToken);
        if (result.IsSuccess) await signIn.SignOutAsync();
        return Respond(result);
    }

    [HttpPost("mfa/disable", Name = "AccountSecurity_DisableMfa")]
    [CookieAntiforgery]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DisableMfa(MfaGrantRequest request, CancellationToken cancellationToken)
    {
        Result<bool> result = await security.DisableAsync(GetContext(), request.Grant, cancellationToken);
        if (!result.IsSuccess) return ToProblem(result);
        await signIn.SignOutAsync();
        return NoContent();
    }

    private AccountSecurityContext GetContext() => currentSession.Proof is { } proof
        ? new(proof.ActorId, proof.SessionHash, proof.SecurityStamp)
        : new(Guid.Empty, "", "");

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
