using System.Buffers;
using System.Net;
using Microsoft.Extensions.Options;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Operator-owned application branding, not recipient-controlled appearance settings.</summary>
public sealed class EmailBrandingOptions
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    public string ApplicationName { get; set; } = "Trykatch Product";
    public string AccentColor { get; set; } = "#315fba";
    public string? LogoUrl { get; set; }

    internal bool IsValid() =>
        !string.IsNullOrWhiteSpace(ApplicationName) && ApplicationName.Length <= 120 && !ApplicationName.Any(char.IsControl) &&
        AccentColor is { Length: 7 } && AccentColor[0] == '#' && AccentColor.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0 &&
        (LogoUrl is null || (LogoUrl.Length <= 2048 && Uri.TryCreate(LogoUrl, UriKind.Absolute, out Uri? logo) &&
            logo.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(logo.Host) && logo.UserInfo.Length == 0));
}

internal sealed class BrandedEmailTemplate(IOptions<EmailBrandingOptions> options)
{
    public string ApplicationName => options.Value.ApplicationName;

    // All dynamic values are encoded here or by the notifier before entering contentHtml.
    // Tables and inline styles deliberately avoid depending on browser-only application CSS.
    public string Render(string title, string preview, string contentHtml, string actionLabel, string actionUrl, string footer)
    {
        EmailBrandingOptions brand = options.Value;
        string name = WebUtility.HtmlEncode(brand.ApplicationName);
        string safeTitle = WebUtility.HtmlEncode(title);
        string safeUrl = WebUtility.HtmlEncode(actionUrl);
        string logo = brand.LogoUrl is null ? "" : $"<img src=\"{WebUtility.HtmlEncode(brand.LogoUrl)}\" width=\"32\" height=\"32\" alt=\"\" style=\"display:inline-block;vertical-align:middle;margin-right:10px;border:0;\">";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{safeTitle}}</title></head>
            <body style="margin:0;padding:0;background-color:#f4f6f5;color:#18201f;font-family:Arial,Helvetica,sans-serif;">
              <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{{WebUtility.HtmlEncode(preview)}}</div>
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background-color:#f4f6f5;"><tr><td align="center" style="padding:32px 16px;">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="max-width:560px;background-color:#ffffff;border:1px solid #dce3e1;border-radius:4px;">
                  <tr><td style="padding:24px;border-bottom:1px solid #dce3e1;border-top:4px solid {{brand.AccentColor}};font-size:20px;font-weight:bold;">{{logo}}{{name}}</td></tr>
                  <tr><td style="padding:28px 24px;font-size:16px;line-height:1.6;">
                    <h1 style="margin:0 0 20px;font-size:26px;line-height:1.3;">{{safeTitle}}</h1>
                    {{contentHtml}}
                    <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:24px 0;"><tr><td bgcolor="{{brand.AccentColor}}" style="border-radius:4px;">
                      <a href="{{safeUrl}}" style="display:inline-block;padding:13px 20px;color:#ffffff;text-decoration:none;font-weight:bold;">{{WebUtility.HtmlEncode(actionLabel)}}</a>
                    </td></tr></table>
                    <p style="margin:0 0 8px;color:#626f6c;font-size:13px;">If the button does not work, copy and paste this link into your browser:</p>
                    <p style="margin:0;font-size:13px;overflow-wrap:anywhere;word-break:break-all;"><a href="{{safeUrl}}" style="color:{{brand.AccentColor}};">{{safeUrl}}</a></p>
                  </td></tr>
                  <tr><td style="padding:20px 24px;border-top:1px solid #dce3e1;color:#626f6c;font-size:13px;line-height:1.6;">{{WebUtility.HtmlEncode(footer)}}</td></tr>
                </table>
                <p style="margin:20px 0 0;color:#626f6c;font-size:12px;">{{name}} &middot; Account notifications</p>
              </td></tr></table>
            </body></html>
            """;
    }
}
