using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;

namespace Trykatch.Identity;

internal sealed class AccountSecurityService(
    IdentityDbContext database,
    UserManager<ApplicationUser> users,
    IUserStore<ApplicationUser> userStore,
    IDataProtectionProvider protection,
    TimeProvider clock) : IAccountSecurity
{
    private readonly IDataProtector pendingKeys = protection.CreateProtector("Trykatch.AccountSecurity.PendingMfa.v1");

    public Task<Result<RecentAssuranceGrant>> ReauthenticateAsync(AccountSecurityContext actor, ReauthenticationCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "reauthenticate", async user =>
        {
            if (!AccountSecurityPurposes.IsAllowed(command.Purpose))
                return Failure<RecentAssuranceGrant>("reauthentication_required");
            if (!await users.HasPasswordAsync(user)) return Failure<RecentAssuranceGrant>("reauthentication_unsupported");
            if (!user.EmailConfirmed || await users.IsLockedOutAsync(user)) return Failure<RecentAssuranceGrant>("reauthentication_failed");
            bool verified = await users.CheckPasswordAsync(user, command.Password);
            if (verified && user.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(command.Code)) verified = false;
                else if (command.IsRecoveryCode)
                {
                    IdentityResult redeemed = await users.RedeemTwoFactorRecoveryCodeAsync(user, command.Code.Trim());
                    if (!redeemed.Succeeded && redeemed.Errors.Any(error => error.Code != nameof(IdentityErrorDescriber.RecoveryCodeRedemptionFailed)))
                        RequireSuccess(redeemed);
                    verified = redeemed.Succeeded;
                }
                else verified = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Normalize(command.Code));
            }
            if (!verified)
            {
                RequireSuccess(await users.AccessFailedAsync(user));
                return Failure<RecentAssuranceGrant>("reauthentication_failed");
            }
            RequireSuccess(await users.ResetAccessFailedCountAsync(user));
            DateTimeOffset now = clock.GetUtcNow();
            await database.Set<RecentAssuranceRecord>().Where(value => value.UserId == user.Id &&
                (value.ExpiresAt <= now || (value.SessionId == actor.SessionId && value.Purpose == command.Purpose)))
                .ExecuteDeleteAsync(cancellationToken);
            string token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            DateTimeOffset expires = now.AddMinutes(5);
            database.Add(new RecentAssuranceRecord
            {
                TokenHash = Hash(token),
                UserId = user.Id,
                SessionId = actor.SessionId,
                SecurityStamp = actor.SecurityStamp,
                Purpose = command.Purpose,
                ExpiresAt = expires
            });
            return Result.Success(new RecentAssuranceGrant(token, expires));
        }, cancellationToken);

    public Task<Result<PendingMfaSetup>> BeginEnrollmentAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "mfa.enrollment.start", async user =>
        {
            string? denied = await ConsumeGrantAsync(actor, grant, user.TwoFactorEnabled ? AccountSecurityPurposes.Replace : AccountSecurityPurposes.Enroll, cancellationToken);
            if (denied is not null) return Failure<PendingMfaSetup>(denied);
            PendingMfaEnrollment? pending = await database.Set<PendingMfaEnrollment>().FindAsync([user.Id], cancellationToken);
            if (pending is not null && pending.ExpiresAt > clock.GetUtcNow()) return Failure<PendingMfaSetup>("enrollment_conflict");
            string key = users.GenerateNewAuthenticatorKey();
            pending ??= new PendingMfaEnrollment { UserId = user.Id, SessionId = "", SecurityStamp = "", ProtectedKey = "" };
            pending.EnrollmentId = Guid.NewGuid();
            pending.SessionId = actor.SessionId;
            pending.SecurityStamp = actor.SecurityStamp;
            pending.ProtectedKey = pendingKeys.Protect(key);
            pending.ExpiresAt = clock.GetUtcNow().AddMinutes(10);
            pending.FailedAttempts = 0;
            if (database.Entry(pending).State == EntityState.Detached) database.Add(pending);
            string label = Uri.EscapeDataString("Trykatch:" + (user.Email ?? user.Id.ToString()));
            return Result.Success(new PendingMfaSetup(pending.EnrollmentId, key, $"otpauth://totp/{label}?secret={key}&issuer=Trykatch&digits=6", pending.ExpiresAt));
        }, cancellationToken);

    public Task<Result<MfaRecoveryCodes>> ConfirmEnrollmentAsync(AccountSecurityContext actor, Guid enrollmentId, string code, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "mfa.enrollment.confirm", async user =>
        {
            PendingMfaEnrollment? pending = await FindPendingAsync(actor, enrollmentId, cancellationToken);
            if (pending is null) return Failure<MfaRecoveryCodes>("enrollment_conflict");
            if (pending.ExpiresAt <= clock.GetUtcNow())
            {
                database.Remove(pending);
                return Failure<MfaRecoveryCodes>("grant_expired");
            }
            if (await users.IsLockedOutAsync(user)) return Failure<MfaRecoveryCodes>("reauthentication_failed");
            string key = pendingKeys.Unprotect(pending.ProtectedKey);
            if (!PendingAuthenticatorCode.Verify(key, Normalize(code), clock.GetUtcNow()))
            {
                pending.FailedAttempts++;
                if (pending.FailedAttempts >= 5) database.Remove(pending);
                RequireSuccess(await users.AccessFailedAsync(user));
                return Failure<MfaRecoveryCodes>("reauthentication_failed");
            }
            RequireSuccess(await users.ResetAccessFailedCountAsync(user));
            await ((IUserAuthenticatorKeyStore<ApplicationUser>)userStore).SetAuthenticatorKeyAsync(user, key, cancellationToken);
            RequireSuccess(await users.SetTwoFactorEnabledAsync(user, true));
            MfaRecoveryCodes codes = await ReplaceRecoveryCodesAsync(user);
            await InvalidateAssuranceAsync(user, cancellationToken);
            return Result.Success(codes);
        }, cancellationToken);

    public Task<Result<bool>> CancelEnrollmentAsync(AccountSecurityContext actor, Guid enrollmentId, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "mfa.enrollment.cancel", async _ =>
        {
            PendingMfaEnrollment? pending = await FindPendingAsync(actor, enrollmentId, cancellationToken);
            if (pending is null) return Failure<bool>("enrollment_conflict");
            database.Remove(pending);
            return Result.Success(true);
        }, cancellationToken);

    public Task<Result<MfaRecoveryCodes>> RegenerateRecoveryCodesAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "mfa.recovery-codes", async user =>
        {
            string? denied = await ConsumeGrantAsync(actor, grant, AccountSecurityPurposes.RecoveryCodes, cancellationToken);
            if (denied is not null) return Failure<MfaRecoveryCodes>(denied);
            if (!user.TwoFactorEnabled) return Failure<MfaRecoveryCodes>("enrollment_conflict");
            MfaRecoveryCodes codes = await ReplaceRecoveryCodesAsync(user);
            await InvalidateAssuranceAsync(user, cancellationToken);
            return Result.Success(codes);
        }, cancellationToken);

    public Task<Result<bool>> DisableAsync(AccountSecurityContext actor, string? grant, CancellationToken cancellationToken) =>
        ExecuteAsync(actor, "mfa.disable", async user =>
        {
            string? denied = await ConsumeGrantAsync(actor, grant, AccountSecurityPurposes.Disable, cancellationToken);
            if (denied is not null) return Failure<bool>(denied);
            if (!user.TwoFactorEnabled) return Failure<bool>("enrollment_conflict");
            RequireSuccess(await users.SetTwoFactorEnabledAsync(user, false));
            RequireSuccess(await users.ResetAuthenticatorKeyAsync(user));
            await ((IUserTwoFactorRecoveryCodeStore<ApplicationUser>)userStore).ReplaceCodesAsync(user, [], cancellationToken);
            RequireSuccess(await users.UpdateAsync(user));
            await InvalidateAssuranceAsync(user, cancellationToken);
            return Result.Success(true);
        }, cancellationToken);

    private async Task<MfaRecoveryCodes> ReplaceRecoveryCodesAsync(ApplicationUser user)
    {
        string[]? codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray();
        if (codes is not { Length: 10 }) throw new SecurityMutationException();
        return new(codes);
    }

    private async Task InvalidateAssuranceAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        RequireSuccess(await users.UpdateSecurityStampAsync(user));
        foreach (var entry in database.ChangeTracker.Entries<RecentAssuranceRecord>().ToArray()) entry.State = EntityState.Detached;
        foreach (var entry in database.ChangeTracker.Entries<PendingMfaEnrollment>().ToArray()) entry.State = EntityState.Detached;
        await database.Set<RecentAssuranceRecord>().Where(value => value.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await database.Set<PendingMfaEnrollment>().Where(value => value.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
    }

    private Task<PendingMfaEnrollment?> FindPendingAsync(AccountSecurityContext actor, Guid enrollmentId, CancellationToken cancellationToken) =>
        database.Set<PendingMfaEnrollment>().SingleOrDefaultAsync(value => value.UserId == actor.UserId && value.EnrollmentId == enrollmentId &&
            value.SessionId == actor.SessionId && value.SecurityStamp == actor.SecurityStamp, cancellationToken);

    private async Task<string?> ConsumeGrantAsync(AccountSecurityContext actor, string? token, string purpose, CancellationToken cancellationToken)
    {
        if (token is null || token.Length != 43) return "reauthentication_required";
        RecentAssuranceRecord? grant = await database.Set<RecentAssuranceRecord>().FindAsync([Hash(token)], cancellationToken);
        if (grant is null || grant.UserId != actor.UserId || grant.SessionId != actor.SessionId || grant.SecurityStamp != actor.SecurityStamp ||
            grant.Purpose != purpose || grant.ConsumedAt is not null) return "reauthentication_required";
        if (grant.ExpiresAt <= clock.GetUtcNow()) return "grant_expired";
        grant.ConsumedAt = clock.GetUtcNow();
        return null;
    }

    private Task<Result<T>> ExecuteAsync<T>(AccountSecurityContext actor, string action, Func<ApplicationUser, Task<Result<T>>> operation, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({actor.UserId.ToString()}, 734003))", cancellationToken);
            database.ChangeTracker.Clear();
            try
            {
                ApplicationUser? user = await users.FindByIdAsync(actor.UserId.ToString());
                Result<T> result = user is null || string.IsNullOrWhiteSpace(actor.SessionId) || string.IsNullOrWhiteSpace(actor.SecurityStamp) ||
                    user.SecurityStamp != actor.SecurityStamp ? Failure<T>("reauthentication_required") : await operation(user);
                database.Add(new AccountSecurityEvent { UserId = actor.UserId, Action = action, Outcome = result.IsSuccess ? "succeeded" : result.ErrorCode!, OccurredAt = clock.GetUtcNow() });
                await database.SaveChangesAsync(cancellationToken);
                // Invalid proofs still commit attempt counters and consumed grants. Any
                // storage/Identity failure instead rolls back the complete mutation.
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (SecurityMutationException) { return Failure<T>("identity_validation"); }
            finally { database.ChangeTracker.Clear(); }
        });

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new SecurityMutationException();
    }

    private static Result<T> Failure<T>(string code) => Result.Failure<T>(code, code switch
    {
        "reauthentication_unsupported" => "This account requires a supported identity-provider recovery flow. Contact your administrator.",
        "grant_expired" => "Verification has expired. Verify your identity again.",
        "enrollment_conflict" => "Enrollment is unavailable or has changed. Start again.",
        _ => "The security operation could not be verified. Verify your identity again."
    });

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Normalize(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
    private sealed class SecurityMutationException : Exception;
}
