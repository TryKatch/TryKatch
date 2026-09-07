using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace FlatpackApp.Infrastructure.Modules.Email;

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
        mail.From.Add(MailboxAddress.Parse(configuration["Email:From"] ?? "Flatpack <noreply@localhost>"));
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

public static class EmailModule
{
    public static IServiceCollection AddFlatpackEmail(this IServiceCollection services) => services.AddScoped<IEmailSender, SmtpEmailSender>();
}
