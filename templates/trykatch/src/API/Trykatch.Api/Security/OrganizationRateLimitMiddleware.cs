using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.Api.Security;

public sealed class OrganizationRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOrganizationContext organization,
        IOrganizationRequestAdmission admission, IAntiforgery antiforgery)
    {
        if (!admission.IsEnabled || !organization.IsResolved
            || context.GetEndpoint()?.Metadata.GetMetadata<IOrganizationScopedMetadata>() is null)
        {
            await next(context);
            return;
        }

        string method = context.Request.Method;
        bool safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
        bool cookie = context.User.Identities.Any(identity => identity.IsAuthenticated
            && identity.AuthenticationType == IdentityConstants.ApplicationScheme);
        if (!safe && cookie)
        {
            IAntiforgeryValidationFeature? validation = context.Features.Get<IAntiforgeryValidationFeature>();
            try
            {
                if (validation is { IsValid: false }) throw new AntiforgeryValidationException("Request verification failed.");
                if (validation is null) await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, title: "Request verification failed",
                    detail: "Refresh the page and try again.").ExecuteAsync(context);
                return;
            }
        }

        OrganizationRequestAdmissionResult result = await admission.TryAdmitAsync(context.RequestAborted);
        if (result.Disposition == OrganizationRequestAdmissionDisposition.Allowed)
        {
            await next(context);
            return;
        }
        if (result.Disposition == OrganizationRequestAdmissionDisposition.Exhausted)
        {
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await Results.Problem(statusCode: 429, title: "organization_request_limit_exhausted").ExecuteAsync(context);
            return;
        }
        await Results.Problem(statusCode: 503, title: "organization_request_limit_unavailable").ExecuteAsync(context);
    }
}
