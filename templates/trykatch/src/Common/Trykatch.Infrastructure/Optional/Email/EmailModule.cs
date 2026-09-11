using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MimeKit;
using Trykatch.Application.Identity;

namespace Trykatch.Infrastructure.Modules.Email;

public sealed record EmailMessage(string Recipient, string Subject, string TextBody, string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>External SMTP-client boundary. Production uses MailKit's normal TLS validation.</summary>
public interface ISmtpClientFactory
{
    SmtpClient CreateClient();
}

internal sealed class SmtpClientFactory : ISmtpClientFactory
{
    public SmtpClient CreateClient() => new() { Timeout = 30_000 };
}

public sealed class EmailDeliveryException() : Exception("Email delivery failed.");

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ISmtpClientFactory clients) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        SmtpOptions settings = options.Value;
        using MimeMessage mail = new();
        mail.From.Add(MailboxAddress.Parse(settings.From));
        mail.To.Add(MailboxAddress.Parse(message.Recipient));
        mail.Subject = message.Subject;
        mail.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using SmtpClient client = clients.CreateClient();
        try
        {
            SecureSocketOptions security = settings.Security switch
            {
                SmtpTransportSecurity.StartTls => SecureSocketOptions.StartTls,
                SmtpTransportSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpTransportSecurity.None => SecureSocketOptions.None,
                _ => throw new InvalidOperationException("Invalid email transport security.")
            };
            await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.Username))
                await client.AuthenticateAsync(settings.Username, settings.Password!, cancellationToken);
            await client.SendAsync(mail, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception failure) when (failure is IOException or System.Net.Sockets.SocketException or MailKit.CommandException or MailKit.ProtocolException or
            MailKit.Security.AuthenticationException or System.Security.Authentication.AuthenticationException or SslHandshakeException or NotSupportedException)
        {
            // SMTP replies and MIME bodies can contain credentials or recovery links.
            // Do not propagate the remote reply/inner exception to application logging.
            throw new EmailDeliveryException();
        }
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
    public static IServiceCollection AddEmailModule(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false, bool isOpenApiGeneration = false)
    {
        services.AddOptions<SmtpOptions>().Configure(options =>
        {
            SmtpOptions configured = SmtpOptions.Load(configuration, isDevelopment || isOpenApiGeneration);
            options.Host = configured.Host;
            options.Port = configured.Port;
            options.From = configured.From;
            options.Security = configured.Security;
            options.Username = configured.Username;
            options.Password = configured.Password;
        }).ValidateOnStart();
        services.TryAddSingleton<ISmtpClientFactory, SmtpClientFactory>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IAccountRecoveryNotifier, SmtpAccountRecoveryNotifier>();
        services.AddScoped<IInvitationNotifier, SmtpInvitationNotifier>();
        return services;
    }
}
