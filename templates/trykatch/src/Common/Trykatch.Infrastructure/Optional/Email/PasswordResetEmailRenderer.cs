using System.Net;
using Trykatch.Application.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class PasswordResetEmailRenderer(PasswordResetEmailRendering rendering) : IPasswordResetEmailRenderer
{
    public EmailPreparedMessage Render(PasswordResetEmailIntent intent) =>
        rendering.Render(intent.Recipient, intent.DisplayName, intent.ResetUrl);
}

/// <summary>One rendering path shared by queued password resets and the synchronous legacy notifier.</summary>
internal sealed class PasswordResetEmailRendering(BrandedEmailTemplate template)
{
    public EmailPreparedMessage Render(string recipient, string displayName, string resetUrl)
    {
        string safeName = WebUtility.HtmlEncode(displayName);
        return new EmailPreparedMessage(
            recipient,
            $"Reset your {template.ApplicationName} password",
            $"Hello {displayName},\n\nUse this link to reset your password:\n{resetUrl}\n\nIf you did not request this, you can safely ignore this message.",
            template.Render("Reset your password", $"Reset your {template.ApplicationName} password.",
                $"<p>Hello {safeName},</p><p>Use the link below to reset your password.</p>", "Reset password", resetUrl,
                "If you did not request this, you can safely ignore this message."));
    }

    public Task SendAsync(IEmailSender emailSender, string recipient, string displayName, string resetUrl,
        CancellationToken cancellationToken) =>
        ToEmailMessage(Render(recipient, displayName, resetUrl), emailSender, cancellationToken);

    private static Task ToEmailMessage(EmailPreparedMessage message, IEmailSender emailSender, CancellationToken cancellationToken) =>
        emailSender.SendAsync(new EmailMessage(message.Recipient, message.Subject, message.TextBody, message.HtmlBody), cancellationToken);
}
