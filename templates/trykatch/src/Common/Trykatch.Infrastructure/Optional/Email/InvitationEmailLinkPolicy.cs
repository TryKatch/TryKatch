using Microsoft.Extensions.Configuration;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Email links use operator-approved configuration, never request origins or host headers.</summary>
internal sealed class InvitationEmailLinkPolicy
{
    private readonly string? publicUrl;

    public InvitationEmailLinkPolicy(IConfiguration configuration, bool isDevelopment)
    {
        string? configured = configuration["Application:PublicUrl"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(configured) || configured.Length > 8120 || configured.Any(char.IsControl)
            || configured.Contains('?') || configured.Contains('#')
            || !Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0
            || !(uri.Scheme == Uri.UriSchemeHttps || isDevelopment && uri.Scheme == Uri.UriSchemeHttp))
            return;
        publicUrl = configured;
    }

    public bool IsAvailable => publicUrl is not null;
    public string Build(string token) => publicUrl is not null
        ? $"{publicUrl}/invite/{Uri.EscapeDataString(token)}"
        : throw new InvalidOperationException("An approved invitation email origin is required.");
    public bool IsValid(string invitationUrl, string token) => IsAvailable && invitationUrl.Length <= 8192
        && string.Equals(invitationUrl, Build(token), StringComparison.Ordinal);
}
