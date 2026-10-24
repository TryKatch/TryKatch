using Microsoft.AspNetCore.Diagnostics;
using Trykatch.Modules;

namespace Trykatch.Api.Security;

public sealed class OrganizationFeatureExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string code, string detail) = exception switch
        {
            OrganizationFeatureUnavailableException => (StatusCodes.Status409Conflict, "feature_unavailable",
                "The requested feature is unavailable in this organization."),
            OrganizationFeaturePolicyUnavailableException => (StatusCodes.Status503ServiceUnavailable, "feature_policy_unavailable",
                "Feature policy is temporarily unavailable. Try again later."),
            _ => (0, "", "")
        };
        if (status == 0) return false;
        httpContext.Response.StatusCode = status;
        await Results.Problem(statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(httpContext);
        return true;
    }
}
