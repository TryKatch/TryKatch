using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Trykatch.Infrastructure.Modules.Email;

public enum SmtpTransportSecurity { StartTls, SslOnConnect, None }

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string From { get; set; } = "";
    public SmtpTransportSecurity? Security { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? PasswordFile { get; set; }

    internal static SmtpOptions Load(IConfiguration configuration, bool isDevelopment)
    {
        try
        {
            SmtpOptions options = new();
            configuration.GetSection("Email").Bind(options);
            string? configuredSecurity = configuration["Email:Security"];
            if (configuredSecurity is not null && !Enum.GetNames<SmtpTransportSecurity>().Contains(configuredSecurity, StringComparer.Ordinal)) throw Invalid();
            if (isDevelopment)
            {
                if (string.IsNullOrWhiteSpace(options.Host)) options.Host = "localhost";
                if (string.IsNullOrWhiteSpace(options.From)) options.From = "Trykatch <noreply@localhost>";
                options.Security ??= SmtpTransportSecurity.StartTls;
            }
            if (string.IsNullOrWhiteSpace(options.Host) || options.Host.Any(char.IsWhiteSpace) || options.Port is < 1 or > 65535 ||
                !MailboxAddress.TryParse(options.From, out MailboxAddress? sender) || !sender.Address.Contains('@', StringComparison.Ordinal) ||
                options.From.Any(char.IsControl) || options.Security is not { } security || !Enum.IsDefined(security) ||
                (!isDevelopment && security == SmtpTransportSecurity.None)) throw Invalid();
            if (!string.IsNullOrEmpty(options.PasswordFile))
            {
                if (!string.IsNullOrEmpty(options.Password)) throw Invalid();
                FileInfo file = new(options.PasswordFile);
                if (!file.Exists || file.Length is 0 or > 4096) throw Invalid();
                options.Password = File.ReadAllText(file.FullName).TrimEnd('\r', '\n');
            }
            if (string.IsNullOrWhiteSpace(options.Username) != string.IsNullOrEmpty(options.Password)) throw Invalid();
            return options;
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            throw Invalid();
        }
    }

    private static OptionsValidationException Invalid() => new("Email", typeof(SmtpOptions),
        ["Email configuration is invalid. Configure Host, Port, From, explicit Security (StartTls or SslOnConnect outside Development), and paired credentials with exactly one password source."]);
}
