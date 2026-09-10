using Microsoft.AspNetCore.DataProtection;

namespace Trykatch.Api.Security;

public interface IWorkspaceContextCookie
{
    bool TryRead(HttpContext context, out Guid organizationId);
    void Write(HttpContext context, Guid organizationId, bool persistent);
    void Clear(HttpContext context);
}

internal sealed class WorkspaceContextCookie(IDataProtectionProvider dataProtectionProvider) : IWorkspaceContextCookie
{
    private const string CookieName = "__Host-trykatch-workspace";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Trykatch.WorkspaceContext.v1");

    public bool TryRead(HttpContext context, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (!context.Request.Cookies.TryGetValue(CookieName, out string? protectedValue)) return false;

        try
        {
            return Guid.TryParse(_protector.Unprotect(protectedValue), out organizationId);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public void Write(HttpContext context, Guid organizationId, bool persistent)
    {
        CookieOptions options = Options();
        if (persistent) options.MaxAge = TimeSpan.FromDays(30);
        context.Response.Cookies.Append(CookieName, _protector.Protect(organizationId.ToString()), options);
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, Options());

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        IsEssential = true,
        Path = "/",
        SameSite = SameSiteMode.Strict,
        Secure = true
    };
}
