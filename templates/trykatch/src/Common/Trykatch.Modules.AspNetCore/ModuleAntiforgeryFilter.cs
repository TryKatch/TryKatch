using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Trykatch.Modules.AspNetCore;

/// <summary>
/// Protects cookie-authenticated module writes, including JSON and bodyless
/// handlers. Antiforgery middleware records failures but does not stop those
/// handlers from executing; the shared module boundary must enforce them.
/// </summary>
internal sealed class ModuleAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        string method = httpContext.Request.Method;
        bool isSafeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
        bool usesApplicationCookie = httpContext.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, IdentityConstants.ApplicationScheme, StringComparison.Ordinal));

        if (!isSafeMethod && usesApplicationCookie)
        {
            IAntiforgeryValidationFeature? validation = httpContext.Features.Get<IAntiforgeryValidationFeature>();
            if (validation is { IsValid: false }) return VerificationFailed();
            try
            {
                // Reuse middleware validation when present. Reading the form
                // again after its recorded failure can itself throw; modules
                // without validation metadata still require an explicit check.
                if (validation is null) await antiforgery.ValidateRequestAsync(httpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return VerificationFailed();
            }
        }

        return await next(context);
    }

    private static IResult VerificationFailed() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Request verification failed",
        detail: "Refresh the page and try again.");
}
