using Microsoft.AspNetCore.Diagnostics;
using Trykatch.Modules;

namespace Trykatch.Api.Security;

public sealed class OrganizationFeatureExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OrganizationFeaturePolicyUnavailableException) return false;
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "feature_policy_unavailable", detail: "Feature policy is temporarily unavailable. Try again later.",
            extensions: new Dictionary<string, object?> { ["code"] = "feature_policy_unavailable" })
            .ExecuteAsync(httpContext);
        return true;
    }
}
