using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Trykatch.Application.Mailing;
using Trykatch.Application.Identity;
using Trykatch.Infrastructure.Mailing;

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

internal sealed class SmtpInvitationNotifier(IEmailSender emailSender, InvitationEmailRendering rendering) : IInvitationNotifier
{
    public bool IsConfigured => true;

    public Task SendOrganizationInvitationAsync(
        string recipient,
        string organizationName,
        string roleName,
        string invitationUrl,
        CancellationToken cancellationToken = default)
    {
        EmailPreparedMessage message = rendering.Render(recipient, organizationName, roleName, invitationUrl);
        return emailSender.SendAsync(new EmailMessage(message.Recipient, message.Subject, message.TextBody, message.HtmlBody), cancellationToken);
    }
}

public static class EmailModule
{
    public static IServiceCollection AddEmailModule(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false, bool isOpenApiGeneration = false)
    {
        services.AddDataProtection();
        services.TryAddSingleton<IEmailPayloadProtector, DataProtectionEmailPayloadProtector>();
        services.AddOptions<EmailBrandingOptions>().Bind(configuration.GetSection("Email:Branding"))
            .Validate(options => options.IsValid(), "Email branding is invalid. Use an application name, a six-digit hex accent color, and an optional HTTPS logo URL.")
            .ValidateOnStart();
        services.TryAddSingleton<BrandedEmailTemplate>();
        services.TryAddSingleton<PasswordResetEmailRendering>();
        services.TryAddSingleton<InvitationEmailRendering>();
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
        if (!isOpenApiGeneration)
            services.TryAddScoped<IEmailTemplateCatalogue, ApprovedEmailTemplateCatalogue>();

        string? workConnection = configuration.GetConnectionString("trykatch-work");
        if (!isOpenApiGeneration && !string.IsNullOrWhiteSpace(workConnection))
        {
            services.TryAddSingleton<DbContextOptions<OrganizationEmailDeliveryDbContext>>(_ =>
                new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>().UseNpgsql(workConnection).Options);
            services.TryAddSingleton<OrganizationEmailDeliveryStore>();
            services.TryAddScoped<EmailTemplateRegistry>();
            services.AddScoped<IEmailDeliveryMetadataReader, OrganizationEmailDeliveryMetadataReader>();
            services.TryAddScoped<OrganizationModuleEmailPolicy>();
            services.TryAddSingleton(new InvitationEmailLinkPolicy(configuration, isDevelopment));
            services.TryAddScoped<OrganizationInvitationEmailPolicy>();
            services.TryAddScoped<IOrganizationInvitationEmailSubmission, OrganizationInvitationEmailQueue>();
            services.TryAddScoped<global::Trykatch.Modules.IModuleEmailQueue, OrganizationModuleEmailQueue>();
            services.AddSingleton<OrganizationEmailDeliverySource>();
            services.AddSingleton<global::Trykatch.Application.Mailing.IEmailDeliverySource>(provider =>
                provider.GetRequiredService<OrganizationEmailDeliverySource>());
        }
        return services;
    }
}
