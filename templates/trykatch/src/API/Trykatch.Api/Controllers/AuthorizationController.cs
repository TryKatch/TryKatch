using System.Security.Claims;
using Trykatch.Identity;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Trykatch.Api.Controllers;

public sealed class AuthorizationController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : Controller
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
    [AcceptVerbs("GET", "POST")]
    [Route("~/connect/authorize", Name = "OpenIddict_Authorize")]
    public async Task<IActionResult> AuthorizeEndpoint()
    {
        OpenIddictRequest request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("OpenID Connect request is unavailable.");
        ApplicationUser user = await users.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated user no longer exists.");

        ClaimsIdentity identity = new(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.Name, user.DisplayName)
            .SetScopes(request.GetScopes());
        identity.SetDestinations(static claim => claim.Type == Claims.Name || claim.Type == Claims.Email
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : [Destinations.AccessToken]);
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpPost("~/connect/token", Name = "OpenIddict_Token")]
    public async Task<IActionResult> TokenEndpoint()
    {
        OpenIddictRequest request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("OpenID Connect request is unavailable.");
        if (request.IsClientCredentialsGrantType())
        {
            ClaimsIdentity identity = new(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, request.ClientId ?? throw new InvalidOperationException("Client ID is required."))
                .SetScopes(request.GetScopes());
            identity.SetDestinations(static _ => [Destinations.AccessToken]);
            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        AuthenticateResult result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal is null) return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        return SignIn(result.Principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [Authorize(AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie)]
    [AcceptVerbs("GET", "POST")]
    [Route("~/connect/logout", Name = "OpenIddict_Logout")]
    public async Task<IActionResult> LogoutEndpoint()
    {
        await signIn.SignOutAsync();
        return SignOut(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
