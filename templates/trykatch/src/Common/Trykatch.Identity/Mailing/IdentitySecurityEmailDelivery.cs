using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Trykatch.Application.Mailing;
using Trykatch.Modules;

namespace Trykatch.Identity;

internal sealed class IdentitySecurityEmailDelivery(
    IdentityEmailDeliveryStore store,
    UserManager<ApplicationUser> users,
    IEmailPayloadProtector protector,
    IPasswordResetEmailRenderer renderer,
    IdentityEmailLinkPolicy linkPolicy,
    TimeProvider timeProvider) : IIdentitySecurityEmailSubmission, IEmailDeliverySource
{
    private const string TemplateKey = "core.password-reset";
    private const int TemplateVersion = 1;
    private const int MaximumRecipientLength = 254;
    private const int MaximumDisplayNameLength = 160;
    private const int MaximumTokenLength = 4096;
    private const int MaximumResetUrlLength = 8192;

    public bool IsConfigured => true;
    public EmailDeliveryBoundary Boundary => EmailDeliveryBoundary.Identity;

    public async Task<ModuleWorkReceipt> EnqueuePasswordResetAsync(
        PasswordResetEmailIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!HasValidRequestShape(intent))
            return Receipt(intent.DeliveryId, ModuleWorkState.Rejected, "invalid_request");
        if (!linkPolicy.IsAvailable)
            return Receipt(intent.DeliveryId, ModuleWorkState.Unavailable, "public_url_unavailable");

        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser? user = await users.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == intent.UserId, cancellationToken);
        UserCheck check = await CheckCurrentUserAsync(user, intent.UserId, intent.Recipient, intent.Token, cancellationToken);
        if (check.Failure is not null)
            return Receipt(intent.DeliveryId, ModuleWorkState.Rejected, check.Failure);
        if (!linkPolicy.IsValid(intent.ResetUrl, check.User!.Email!, intent.Token))
            return Receipt(intent.DeliveryId, ModuleWorkState.Rejected, "source_denied");

        PasswordResetEmailIntent canonical = intent with
        {
            Recipient = check.User.Email!,
            DisplayName = check.User.DisplayName
        };
        EmailDeliveryProtectionContext context = ProtectionContext(canonical.UserId, canonical.DeliveryId);
        string protectedIntent;
        try
        {
            string serialized = JsonSerializer.Serialize(canonical);
            protectedIntent = protector.Protect(context, serialized);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            return Receipt(intent.DeliveryId, ModuleWorkState.Unavailable, "protection_unavailable");
        }

        IdentityEmailDeliveryRecord record = new()
        {
            Id = canonical.DeliveryId,
            UserId = canonical.UserId,
            TemplateKey = TemplateKey,
            TemplateVersion = TemplateVersion,
            ProtectedIntent = protectedIntent,
            State = "queued",
            Attempts = 0,
            CreatedAtUtc = timeProvider.GetUtcNow().ToUniversalTime()
        };

        try
        {
            if (await store.TryInsertAsync(record, cancellationToken))
                return Receipt(canonical.DeliveryId, ModuleWorkState.Accepted, accepted: timeProvider.GetUtcNow());

            return await ResolveExistingAsync(canonical, cancellationToken);
        }
        catch (Exception exception) when (IsUncertainWrite(exception))
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                IdentityEmailDeliveryRecord? persisted = await store.FindAsync(canonical.DeliveryId, timeout.Token);
                if (persisted is not null)
                    return IsEquivalentIntent(persisted, canonical)
                        ? Receipt(canonical.DeliveryId, ModuleWorkState.Accepted, accepted: persisted.CreatedAtUtc)
                        : Receipt(canonical.DeliveryId, ModuleWorkState.Rejected, "delivery_id_conflict");
            }
            catch (Exception recoveryFailure) when (IsUncertainWrite(recoveryFailure))
            {
                // Preserve uncertainty as a closed receipt; never retry the write or expose its failure.
            }
            return Receipt(canonical.DeliveryId, ModuleWorkState.Unavailable, "delivery_state_uncertain");
        }
    }

    public Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken) =>
        store.ClaimAsync(limit, cancellationToken);

    public async Task<EmailDeliveryPreparation> PrepareAsync(EmailDeliveryLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsValidLeaseEnvelope(lease))
            return Suppressed(EmailFailureCode.SourceDenied);

        IdentityEmailDeliveryRecord? record = await store.FindAsync(lease.Context.DeliveryId, cancellationToken);
        if (record is null || record.State != "leased"
            || record.Id != lease.Context.DeliveryId
            || record.UserId != lease.Context.BoundaryOwnerId
            || record.TemplateKey != TemplateKey || record.TemplateVersion != TemplateVersion
            || record.LeaseId != lease.LeaseId || record.Attempts != lease.Attempts
            || record.LeaseUntilUtc is null || record.LeaseUntilUtc <= timeProvider.GetUtcNow())
            return Suppressed(EmailFailureCode.SourceDenied);
        if (!string.Equals(record.ProtectedIntent, lease.ProtectedIntent, StringComparison.Ordinal))
            return Suppressed(EmailFailureCode.PayloadInvalid);

        PasswordResetEmailIntent? intent = TryUnprotect(record);
        if (intent is null || !HasValidRequestShape(intent)
            || intent.DeliveryId != record.Id || intent.UserId != record.UserId)
            return Suppressed(EmailFailureCode.PayloadInvalid);

        ApplicationUser? user = await users.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == intent.UserId, cancellationToken);
        UserCheck check = await CheckCurrentUserAsync(user, intent.UserId, intent.Recipient, intent.Token, cancellationToken);
        if (check.Failure is not null)
            return Suppressed(ToEmailFailure(check.Failure));
        if (!linkPolicy.IsValid(intent.ResetUrl, check.User!.Email!, intent.Token))
            return Suppressed(EmailFailureCode.SourceDenied);

        PasswordResetEmailIntent renderIntent = intent with
        {
            Recipient = check.User.Email!,
            DisplayName = check.User.DisplayName
        };
        try
        {
            EmailPreparedMessage? prepared = renderer.Render(renderIntent);
            if (prepared is null)
                return new EmailDeliveryPreparation(EmailPreparationDisposition.RetryableFailure, FailureCode: EmailFailureCode.SourceUnavailable);
            return new EmailDeliveryPreparation(
                EmailPreparationDisposition.Ready,
                prepared with { Recipient = check.User.Email! });
        }
        catch (Exception exception) when (IsExpectedRendererFailure(exception))
        {
            return new EmailDeliveryPreparation(EmailPreparationDisposition.RetryableFailure, FailureCode: EmailFailureCode.SourceUnavailable);
        }
    }

    public Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken) =>
        store.CompleteAsync(lease, outcome, cancellationToken);

    private async Task<ModuleWorkReceipt> ResolveExistingAsync(
        PasswordResetEmailIntent canonical,
        CancellationToken cancellationToken)
    {
        IdentityEmailDeliveryRecord? existing = await store.FindAsync(canonical.DeliveryId, cancellationToken);
        if (existing is null)
            return Receipt(canonical.DeliveryId, ModuleWorkState.Unavailable, "delivery_state_uncertain");
        return IsEquivalentIntent(existing, canonical)
            ? Receipt(canonical.DeliveryId, ModuleWorkState.AlreadyAccepted, accepted: existing.CreatedAtUtc)
            : Receipt(canonical.DeliveryId, ModuleWorkState.Rejected, "delivery_id_conflict");
    }

    private bool IsEquivalentIntent(IdentityEmailDeliveryRecord record, PasswordResetEmailIntent canonical)
    {
        if (record.Id != canonical.DeliveryId || record.UserId != canonical.UserId
            || record.TemplateKey != TemplateKey || record.TemplateVersion != TemplateVersion)
            return false;
        PasswordResetEmailIntent? persisted = TryUnprotect(record);
        return persisted is not null
               && persisted.DeliveryId == canonical.DeliveryId
               && persisted.UserId == canonical.UserId
               && string.Equals(persisted.Recipient, canonical.Recipient, StringComparison.Ordinal)
               && string.Equals(persisted.DisplayName, canonical.DisplayName, StringComparison.Ordinal)
               && string.Equals(persisted.Token, canonical.Token, StringComparison.Ordinal)
               && string.Equals(persisted.ResetUrl, canonical.ResetUrl, StringComparison.Ordinal);
    }

    private PasswordResetEmailIntent? TryUnprotect(IdentityEmailDeliveryRecord record)
    {
        try
        {
            string json = protector.Unprotect(ProtectionContext(record.UserId, record.Id), record.ProtectedIntent);
            return JsonSerializer.Deserialize<PasswordResetEmailIntent>(json);
        }
        catch (Exception exception) when (IsProtectionFailure(exception))
        {
            return null;
        }
    }

    private async Task<UserCheck> CheckCurrentUserAsync(
        ApplicationUser? user,
        Guid expectedUserId,
        string? recipient,
        string? token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (user is null || user.Id != expectedUserId || user.IsPlatformAccessSuspended)
            return new(null, "source_denied");
        if (!user.EmailConfirmed || string.IsNullOrEmpty(user.Email)
            || !IsValidRecipient(user.Email)
            || !string.Equals(user.Email, recipient, StringComparison.OrdinalIgnoreCase))
            return new(null, "recipient_changed");
        if (!IsValidDisplayName(user.DisplayName))
            return new(null, "source_denied");
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength || token.Any(char.IsControl))
            return new(null, "token_invalid");

        bool validToken = await users.VerifyUserTokenAsync(
            user,
            users.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            token);
        cancellationToken.ThrowIfCancellationRequested();
        return validToken ? new(user, null) : new(null, "token_invalid");
    }

    private static bool HasValidRequestShape(PasswordResetEmailIntent intent) =>
        intent.DeliveryId != Guid.Empty && intent.UserId != Guid.Empty
        && IsValidRecipient(intent.Recipient)
        && IsValidDisplayName(intent.DisplayName)
        && !string.IsNullOrEmpty(intent.Token) && intent.Token.Length <= MaximumTokenLength
        && !intent.Token.Any(char.IsControl)
        && !string.IsNullOrEmpty(intent.ResetUrl) && intent.ResetUrl.Length <= MaximumResetUrlLength
        && !intent.ResetUrl.Any(char.IsControl);

    private static bool IsValidRecipient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumRecipientLength || value.Any(char.IsControl))
            return false;
        try
        {
            System.Net.Mail.MailAddress parsed = new(value);
            return string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidDisplayName(string? value) =>
        value is not null && value.Length <= MaximumDisplayNameLength && !value.Any(char.IsControl);

    private static bool IsValidLeaseEnvelope(EmailDeliveryLease lease) =>
        lease.Context is not null
        && lease.Context.Boundary == EmailDeliveryBoundary.Identity
        && lease.Context.BoundaryOwnerId != Guid.Empty && lease.Context.DeliveryId != Guid.Empty
        && lease.Context.TemplateKey == TemplateKey && lease.Context.TemplateVersion == TemplateVersion
        && lease.LeaseId != Guid.Empty && lease.Attempts is >= 1 and <= EmailDeliveryPolicy.MaximumAttempts
        && !string.IsNullOrEmpty(lease.ProtectedIntent) && lease.ProtectedIntent.Length <= 262144;

    private static EmailDeliveryProtectionContext ProtectionContext(Guid userId, Guid deliveryId) =>
        new(EmailDeliveryBoundary.Identity, userId, deliveryId, TemplateKey, TemplateVersion);

    private static ModuleWorkReceipt Receipt(
        Guid id,
        ModuleWorkState state,
        string? failureCode = null,
        DateTimeOffset? accepted = null) => new(id, state, accepted?.ToUniversalTime(), failureCode);

    private static EmailDeliveryPreparation Suppressed(EmailFailureCode failureCode) =>
        new(EmailPreparationDisposition.Suppressed, FailureCode: failureCode);

    private static EmailFailureCode ToEmailFailure(string failure) => failure switch
    {
        "recipient_changed" => EmailFailureCode.RecipientChanged,
        "token_invalid" => EmailFailureCode.TokenInvalid,
        _ => EmailFailureCode.SourceDenied
    };

    private static bool IsUncertainWrite(Exception exception) =>
        exception is DbException or IOException or TimeoutException or OperationCanceledException;

    private static bool IsProtectionFailure(Exception exception) =>
        exception is CryptographicException or JsonException or ArgumentException or FormatException;

    private static bool IsExpectedRendererFailure(Exception exception) =>
        exception is JsonException or ArgumentException or FormatException or InvalidOperationException;

    private sealed record UserCheck(ApplicationUser? User, string? Failure);
}
