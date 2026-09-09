using Microsoft.AspNetCore.Antiforgery;
using TrykatchApp.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TrykatchApp.Api.Security;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CookieAntiforgeryAttribute() : TypeFilterAttribute(typeof(CookieAntiforgeryFilter));

public sealed class CookieAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        bool usesApplicationCookie = context.HttpContext.User.Identities.Any(identity =>
            identity.IsAuthenticated && string.Equals(identity.AuthenticationType, TrykatchAuthenticationSchemes.ApplicationCookie, StringComparison.Ordinal));
        if (usesApplicationCookie)
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
    }
}
