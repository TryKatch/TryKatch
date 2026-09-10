using MailKit.Net.Smtp;
using Trykatch.Application.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Trykatch.Infrastructure.Modules.Email;

public sealed record EmailMessage(string Recipient, string Subject, string TextBody, string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

internal sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        MimeMessage mail = new();
        mail.From.Add(MailboxAddress.Parse(configuration["Email:From"] ?? "Trykatch <noreply@localhost>"));
        mail.To.Add(MailboxAddress.Parse(message.Recipient));
        mail.Subject = message.Subject;
        mail.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using SmtpClient client = new();
        await client.ConnectAsync(configuration["Email:Host"] ?? "localhost", configuration.GetValue("Email:Port", 1025), false, cancellationToken);
        string? username = configuration["Email:Username"];
        if (!string.IsNullOrWhiteSpace(username))
        {
            await client.AuthenticateAsync(username, configuration["Email:Password"] ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

internal sealed class SmtpAccountRecoveryNotifier(IEmailSender emailSender) : IAccountRecoveryNotifier
{
    public bool IsConfigured => true;

    public Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default)
    {
        string safeName = System.Net.WebUtility.HtmlEncode(displayName);
        string safeUrl = System.Net.WebUtility.HtmlEncode(resetUrl);
        return emailSender.SendAsync(new EmailMessage(
            recipient,
            "Reset your Trykatch password",
            $"Hello {displayName},\n\nUse this link to reset your password:\n{resetUrl}\n\nIf you did not request this, you can safely ignore this message.",
            $"<p>Hello {safeName},</p><p>Use the link below to reset your password.</p><p><a href=\"{safeUrl}\">Reset password</a></p><p>If you did not request this, you can safely ignore this message.</p>"),
            cancellationToken);
    }
}

internal sealed class SmtpInvitationNotifier(IEmailSender emailSender) : IInvitationNotifier
{
    public bool IsConfigured => true;

    public Task SendOrganizationInvitationAsync(
        string recipient,
        string organizationName,
        string invitationUrl,
        CancellationToken cancellationToken = default)
    {
        string safeOrganization = System.Net.WebUtility.HtmlEncode(organizationName);
        string safeUrl = System.Net.WebUtility.HtmlEncode(invitationUrl);
        return emailSender.SendAsync(new EmailMessage(
            recipient,
            $"Join {organizationName} on Trykatch",
            $"You have been invited to join {organizationName}.\n\nAccept the invitation and set up your account:\n{invitationUrl}\n\nThis invitation can be accepted once. If you were not expecting it, you can safely ignore this message.",
            $"<p>You have been invited to join <strong>{safeOrganization}</strong>.</p><p><a href=\"{safeUrl}\">Accept invitation</a></p><p>This invitation can be accepted once. If you were not expecting it, you can safely ignore this message.</p>"),
            cancellationToken);
    }
}

public static class EmailModule
{
    public static IServiceCollection AddEmailModule(this IServiceCollection services)
    {
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IAccountRecoveryNotifier, SmtpAccountRecoveryNotifier>();
        services.AddScoped<IInvitationNotifier, SmtpInvitationNotifier>();
        return services;
    }
}
