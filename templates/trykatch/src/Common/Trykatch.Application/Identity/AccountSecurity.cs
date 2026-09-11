using Trykatch.Application.Common;

namespace Trykatch.Application.Identity;

public static class AccountSecurityPurposes
{
    public const string Enroll = "mfa.enroll";
    public const string Replace = "mfa.replace";
    public const string Disable = "mfa.disable";
    public const string RecoveryCodes = "mfa.recovery-codes";
    public static bool IsAllowed(string purpose) => purpose is Enroll or Replace or Disable or RecoveryCodes;
}

// Adapters construct this from the authenticated cookie ticket, never request JSON.
public sealed record AccountSecurityContext(Guid UserId, string SessionId, string SecurityStamp);
public sealed record ReauthenticationCommand(string Purpose, string Password, string? Code, bool IsRecoveryCode);
public sealed record RecentAssuranceGrant(string Grant, DateTimeOffset ExpiresAt);
public sealed record PendingMfaSetup(Guid EnrollmentId, string SharedKey, string AuthenticatorUri, DateTimeOffset ExpiresAt);
public sealed record MfaRecoveryCodes(IReadOnlyList<string> Codes, bool SignInRequired = true);

public interface IAccountSecurity
{
    Task<Result<RecentAssuranceGrant>> ReauthenticateAsync(AccountSecurityContext actor, ReauthenticationCommand command, CancellationToken cancellationToken);
    Task<Result<PendingMfaSetup>> BeginEnrollmentAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken);
    Task<Result<MfaRecoveryCodes>> ConfirmEnrollmentAsync(AccountSecurityContext actor, Guid enrollmentId, string code, CancellationToken cancellationToken);
    Task<Result<bool>> CancelEnrollmentAsync(AccountSecurityContext actor, Guid enrollmentId, CancellationToken cancellationToken);
    Task<Result<MfaRecoveryCodes>> RegenerateRecoveryCodesAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken);
    Task<Result<bool>> DisableAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken);
}
