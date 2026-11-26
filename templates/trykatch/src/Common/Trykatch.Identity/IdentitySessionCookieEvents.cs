using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trykatch.Application.Identity;

namespace Trykatch.Identity;

internal sealed class IdentitySessionFeature
{
    public AccountSessionProof? Candidate { get; set; }
    public AccountSessionProof? Validated { get; set; }
    public bool SlidingRenewalRequested { get; set; }
    public bool SignedOut { get; set; }
}

internal sealed class CurrentAccountSession(IHttpContextAccessor accessor) : ICurrentAccountSession
{
    public AccountSessionProof? Proof => accessor.HttpContext?.Features.Get<IdentitySessionFeature>()?.Validated;
}

internal static class IdentitySessionCookieEvents
{
    public static void Configure(CookieAuthenticationOptions options)
    {
        CookieAuthenticationEvents events = options.Events;
        var validate = events.OnValidatePrincipal;
        var sliding = events.OnCheckSlidingExpiration;
        var signingIn = events.OnSigningIn;
        var signingOut = events.OnSigningOut;
        events.OnCheckSlidingExpiration = async context =>
        {
            await sliding(context);
            Feature(context.HttpContext).SlidingRenewalRequested = context.ShouldRenew;
        };
        events.OnValidatePrincipal = context => ValidateAsync(context, validate);
        events.OnSigningIn = async context =>
        {
            await signingIn(context);
            await IssueAsync(context);
        };
        events.OnSigningOut = async context =>
        {
            IdentitySessionFeature feature = Feature(context.HttpContext);
            AccountSessionProof? proof = feature.Candidate ?? feature.Validated;
            feature.SignedOut = true;
            feature.Candidate = feature.Validated = null;
            if (proof is not null)
                await Registry(context.HttpContext).RevokeAsync(proof, context.HttpContext.RequestAborted);
            // Never AuthenticateAsync here: the stamp validator may sign out inside authentication.
            await signingOut(context);
        };
    }

    private static async Task ValidateAsync(CookieValidatePrincipalContext context, Func<CookieValidatePrincipalContext, Task> prior)
    {
        HttpContext http = context.HttpContext;
        IdentitySessionFeature feature = Feature(http);
        AccountSessionProof? proof = ReadProof(http, context.Principal, context.Properties);
        // Install the protected-ticket candidate before the existing stamp validator can sign out.
        feature.Candidate = proof;
        feature.Validated = null;
        DateTimeOffset? issuedAt = context.Properties.IssuedUtc;
        try
        {
            if (proof is not null) await prior(context);
            AccountSessionProof? refreshed = ReadProof(http, context.Principal, context.Properties);
            if (proof is null || feature.SignedOut || refreshed is null
                || proof.ActorId != refreshed.ActorId || proof.SecurityStamp != refreshed.SecurityStamp)
            {
                await RejectAsync(context);
                return;
            }
            IdentitySessionRegistry registry = Registry(http);
            AccountSessionState? state;
            if (feature.SlidingRenewalRequested || context.ShouldRenew)
            {
                TimeSpan lifetime = issuedAt.HasValue ? proof.TicketExpiresAt - issuedAt.Value : TimeSpan.Zero;
                state = lifetime > TimeSpan.Zero
                    ? await registry.RenewAsync(proof, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() + lifetime, http.RequestAborted)
                    : null;
            }
            else state = await registry.ValidateAsync(proof, http.RequestAborted);
            if (state is null)
            {
                await RejectAsync(context);
                return;
            }
            // Capture this ticket's expiry; later HTTP renewal cannot extend a live connection's proof.
            feature.Validated = proof;
        }
        catch
        {
            // RejectPrincipal alone does not cancel a sliding renewal already requested by the handler.
            await RejectAsync(context);
            throw;
        }
    }

    private static async Task IssueAsync(CookieSigningInContext context)
    {
        HttpContext http = context.HttpContext;
        IdentitySessionFeature feature = Feature(http);
        var claims = http.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        if (!Guid.TryParse(context.Principal?.FindFirstValue(claims.UserIdClaimType), out Guid actorId)
            || actorId == Guid.Empty || context.Properties.ExpiresUtc is not { } expiry
            || context.Principal?.FindFirstValue(claims.SecurityStampClaimType) is not { Length: > 0 } stamp)
            throw new InvalidOperationException("Session issuance requires a current account ticket.");
        IdentitySessionRegistry registry = Registry(http);
        AccountSessionProof proof;
        if (context.Properties.Items.ContainsKey(AccountSecuritySession.PropertyName))
        {
            // Explicit refresh can retain a currently valid session, but can never insert a missing row.
            AccountSessionProof? supplied = ReadProof(http, context.Principal, context.Properties);
            AccountSessionProof? current = feature.Validated;
            if (supplied is null || current is null || supplied.ActorId != current.ActorId
                || supplied.SessionHash != current.SessionHash || supplied.SecurityStamp != current.SecurityStamp
                || await registry.RenewAsync(current, expiry, http.RequestAborted) is null)
                throw new InvalidOperationException("Session renewal requires a current session.");
            proof = supplied;
        }
        else
        {
            string identity = Guid.NewGuid().ToString("N");
            proof = await registry.IssueAsync(actorId, Hash(identity), stamp, expiry, feature.Candidate, http.RequestAborted);
            context.Properties.Items.Add(AccountSecuritySession.PropertyName, identity);
        }
        feature.SignedOut = false;
        feature.SlidingRenewalRequested = false;
        feature.Candidate = feature.Validated = proof;
    }

    private static AccountSessionProof? ReadProof(HttpContext http, ClaimsPrincipal? principal, AuthenticationProperties properties)
    {
        var claims = http.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        if (principal?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirstValue(claims.UserIdClaimType), out Guid actor) || actor == Guid.Empty
            || principal.FindFirstValue(claims.SecurityStampClaimType) is not { Length: > 0 and <= 256 } stamp
            || properties.ExpiresUtc is not { } expiry
            || !properties.Items.TryGetValue(AccountSecuritySession.PropertyName, out string? identity)
            || !Guid.TryParseExact(identity, "N", out Guid session) || session == Guid.Empty) return null;
        return new(actor, Hash(identity!), stamp, expiry);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    private static string Hash(string identity) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    private static IdentitySessionRegistry Registry(HttpContext http) => http.RequestServices.GetRequiredService<IdentitySessionRegistry>();
    private static IdentitySessionFeature Feature(HttpContext http)
    {
        IdentitySessionFeature? feature = http.Features.Get<IdentitySessionFeature>();
        if (feature is not null) return feature;
        feature = new();
        http.Features.Set(feature);
        return feature;
    }
}
