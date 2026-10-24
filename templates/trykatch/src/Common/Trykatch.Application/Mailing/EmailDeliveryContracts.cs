using Trykatch.Modules;

namespace Trykatch.Application.Mailing;

public enum EmailDeliveryStatus { Queued, Leased, Delivered, Suppressed, Terminal }
public enum EmailAttemptDisposition { Acknowledged, RetryableFailure, PermanentFailure, Uncertain, Suppressed }
public enum EmailPreparationDisposition { Ready, Suppressed, RetryableFailure }

public enum EmailFailureCode
{
    None, SourceUnavailable, SourceDenied, TemplateUnavailable, RecipientChanged, TokenInvalid, PayloadInvalid,
    InvalidConfiguration, ConnectionFailed, AuthenticationFailed, CertificateValidationFailed, SmtpRejected,
    ConnectionLost, ProtocolFailure, AttemptsExhausted
}

public sealed record EmailAttemptOutcome(
    EmailAttemptDisposition Disposition,
    EmailFailureCode FailureCode = EmailFailureCode.None,
    int? SmtpStatusCode = null);

public sealed record EmailDeliveryTransition(EmailDeliveryStatus Status, DateTimeOffset? NextAttemptAtUtc);

/// <summary>One confirmed committed lease; ciphertext is never an operator response.</summary>
public sealed record EmailDeliveryLease(
    EmailDeliveryProtectionContext Context,
    Guid LeaseId,
    int Attempts,
    DateTimeOffset LeaseUntilUtc,
    string ProtectedIntent)
{
    public override string ToString() => $"Email delivery {Context.DeliveryId:N}, lease {LeaseId:N}";
}

public sealed record EmailPreparedMessage(string Recipient, string Subject, string TextBody, string? HtmlBody = null)
{
    public override string ToString() => "Prepared email (private content)";
}

public sealed record EmailDeliveryPreparation(
    EmailPreparationDisposition Disposition,
    EmailPreparedMessage? Message = null,
    EmailFailureCode FailureCode = EmailFailureCode.None);

/// <summary>Each owned store claims, prepares under current disclosure policy, and acknowledges its own records.</summary>
public interface IEmailDeliverySource
{
    EmailDeliveryBoundary Boundary { get; }
    Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken);
    Task<EmailDeliveryPreparation> PrepareAsync(EmailDeliveryLease lease, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>Trusted host intent; it is never returned in a public password-recovery response.</summary>
public sealed record PasswordResetEmailIntent(
    Guid DeliveryId, Guid UserId, string Recipient, string DisplayName, string Token, string ResetUrl)
{
    public override string ToString() => $"Password reset delivery {DeliveryId:N}";
}

public interface IIdentitySecurityEmailSubmission
{
    bool IsConfigured { get; }
    Task<ModuleWorkReceipt> EnqueuePasswordResetAsync(PasswordResetEmailIntent intent, CancellationToken cancellationToken);
}

/// <summary>One retry/lease policy shared by owned persistence implementations and the dispatcher.</summary>
public static class EmailDeliveryPolicy
{
    public const int MaximumAttempts = 5;
    public static TimeSpan LeaseDuration { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan AttemptTimeout { get; } = TimeSpan.FromSeconds(45);

    public static EmailDeliveryTransition Complete(EmailAttemptOutcome outcome, int attempts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (attempts is < 1 or > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (!Enum.IsDefined(outcome.Disposition) || !Enum.IsDefined(outcome.FailureCode)
            || !HasConsistentSmtpStatus(outcome)
            || (outcome.Disposition == EmailAttemptDisposition.Acknowledged && outcome.FailureCode != EmailFailureCode.None)
            || (outcome.Disposition != EmailAttemptDisposition.Acknowledged && outcome.FailureCode == EmailFailureCode.None))
            throw new ArgumentException("Email attempt outcome is invalid.", nameof(outcome));

        return outcome.Disposition switch
        {
            EmailAttemptDisposition.Acknowledged => new(EmailDeliveryStatus.Delivered, null),
            EmailAttemptDisposition.Suppressed => new(EmailDeliveryStatus.Suppressed, null),
            EmailAttemptDisposition.PermanentFailure => new(EmailDeliveryStatus.Terminal, null),
            EmailAttemptDisposition.RetryableFailure or EmailAttemptDisposition.Uncertain when attempts < MaximumAttempts =>
                new(EmailDeliveryStatus.Queued, now.ToUniversalTime().AddSeconds(5 * (1 << (attempts - 1)))),
            _ => new(EmailDeliveryStatus.Terminal, null)
        };
    }

    private static bool HasConsistentSmtpStatus(EmailAttemptOutcome outcome) => outcome.SmtpStatusCode is not int status
        ? outcome.FailureCode != EmailFailureCode.SmtpRejected
        : outcome.Disposition switch
        {
            EmailAttemptDisposition.Acknowledged => status is >= 200 and < 300,
            EmailAttemptDisposition.RetryableFailure => status is >= 400 and < 500,
            EmailAttemptDisposition.PermanentFailure => status is >= 500 and < 600,
            _ => false
        };
}
