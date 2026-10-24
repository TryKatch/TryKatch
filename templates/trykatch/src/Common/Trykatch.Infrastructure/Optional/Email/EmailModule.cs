using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Trykatch.Application.Mailing;
using Trykatch.Application.Identity;

namespace Trykatch.Infrastructure.Modules.Email;

public sealed record EmailMessage(string Recipient, string Subject, string TextBody, string? HtmlBody = null)
{
    public override string ToString() => "Email message (private content)";
}

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

internal sealed class SmtpEmailSender(IEmailTransport transport) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        EmailTransportResult result = await transport.SendAsync(message, cancellationToken);
        if (result.Disposition != EmailTransportDisposition.Acknowledged)
            throw new EmailDeliveryException();
    }
}

internal sealed class SmtpAccountRecoveryNotifier(IEmailSender emailSender, PasswordResetEmailRendering rendering) : IAccountRecoveryNotifier
{
    public bool IsConfigured => true;

    public Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default) =>
        rendering.SendAsync(emailSender, recipient, displayName, resetUrl, cancellationToken);
}

internal sealed class SmtpInvitationNotifier(IEmailSender emailSender, BrandedEmailTemplate template) : IInvitationNotifier
{
    public bool IsConfigured => true;

    public Task SendOrganizationInvitationAsync(
        string recipient,
        string organizationName,
        string roleName,
        string invitationUrl,
        CancellationToken cancellationToken = default)
    {
        string safeOrganization = System.Net.WebUtility.HtmlEncode(organizationName);
        string safeRole = System.Net.WebUtility.HtmlEncode(roleName);
        return emailSender.SendAsync(new EmailMessage(
            recipient,
            $"Join {organizationName} on {template.ApplicationName}",
            $"You have been invited to join {organizationName}.\n\nWorkspace role: {roleName}\n\nAccept the invitation and set up your account:\n{invitationUrl}\n\nThis invitation can be accepted once. If you were not expecting it, you can safely ignore this message.",
            template.Render("You're invited", $"Join {organizationName} as {roleName}.",
                $"<p>You have been invited to join <strong>{safeOrganization}</strong>.</p><p style=\"padding:16px;background-color:#f8faf9;border:1px solid #dce3e1;border-radius:4px;\">Workspace role: <strong>{safeRole}</strong></p><p>Accept the invitation and set up your account to get started.</p>",
                "Accept invitation", invitationUrl,
                "This invitation can be accepted once. If you were not expecting it, you can safely ignore this message.")),
            cancellationToken);
    }
}

public static class EmailModule
{
    public static IServiceCollection AddEmailModule(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false, bool isOpenApiGeneration = false)
    {
        services.AddOptions<EmailBrandingOptions>().Bind(configuration.GetSection("Email:Branding"))
            .Validate(options => options.IsValid(), "Email branding is invalid. Use an application name, a six-digit hex accent color, and an optional HTTPS logo URL.")
            .ValidateOnStart();
        services.TryAddSingleton<BrandedEmailTemplate>();
        services.TryAddSingleton<PasswordResetEmailRendering>();
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
        services.AddScoped<IEmailTransport, SmtpEmailTransport>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddScoped<IPasswordResetEmailRenderer, PasswordResetEmailRenderer>();
        services.AddScoped<IAccountRecoveryNotifier, SmtpAccountRecoveryNotifier>();
        services.AddScoped<IInvitationNotifier, SmtpInvitationNotifier>();
        return services;
    }
}
