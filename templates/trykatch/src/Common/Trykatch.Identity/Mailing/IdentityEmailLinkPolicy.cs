using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace Trykatch.Identity;

internal sealed class IdentityEmailLinkPolicy
{
    private const string ResetRoute = "/reset-password";
    private readonly Uri? _origin;
    private readonly string _pathBase = string.Empty;
    private readonly bool _isAvailable;
    private readonly bool _allowAnyLoopbackOrigin;

    public IdentityEmailLinkPolicy(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configured = configuration["Application:PublicUrl"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (isDevelopment)
            {
                _allowAnyLoopbackOrigin = true;
                _pathBase = string.Empty;
                _isAvailable = true;
            }
            return;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? publicUri)
            || string.IsNullOrEmpty(publicUri.Host)
            || !string.IsNullOrEmpty(publicUri.UserInfo)
            || publicUri.Query.Length != 0
            || publicUri.Fragment.Length != 0
            || configured.Contains('?')
            || configured.Contains('#')
            || !(publicUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                 || isDevelopment && publicUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            return;

        _origin = publicUri;
        _pathBase = publicUri.AbsolutePath.TrimEnd('/');
        _isAvailable = true;
    }

    public bool IsAvailable => _isAvailable;

    public bool IsValid(string? resetUrl, string? recipient, string? rawToken)
    {
        if (!_isAvailable || string.IsNullOrEmpty(resetUrl) || resetUrl.Length > 8192
            || resetUrl.Any(char.IsControl) || resetUrl.Contains('#')
            || string.IsNullOrEmpty(recipient) || string.IsNullOrEmpty(rawToken)
            || !Uri.TryCreate(resetUrl, UriKind.Absolute, out Uri? candidate)
            || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0
            || !HasAllowedOrigin(candidate)
            || !string.Equals(candidate.AbsolutePath, _pathBase + ResetRoute, StringComparison.Ordinal))
            return false;

        var query = QueryHelpers.ParseQuery(candidate.Query);
        if (query.Count != 2
            || !query.TryGetValue("email", out var emails) || emails.Count != 1
            || !query.TryGetValue("token", out var tokens) || tokens.Count != 1
            || query.Keys.Any(key => key is not ("email" or "token")))
            return false;

        string expectedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));
        return string.Equals(emails[0], recipient, StringComparison.OrdinalIgnoreCase)
               && string.Equals(tokens[0], expectedToken, StringComparison.Ordinal);
    }

    private bool HasAllowedOrigin(Uri candidate) => _allowAnyLoopbackOrigin
        ? candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && candidate.IsLoopback
        : _origin is not null && SameOrigin(_origin, candidate);

    private static bool SameOrigin(Uri expected, Uri candidate) =>
        string.Equals(expected.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
        && expected.Port == candidate.Port;
}
