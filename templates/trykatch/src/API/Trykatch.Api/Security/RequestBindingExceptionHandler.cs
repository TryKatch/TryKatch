using Microsoft.AspNetCore.Diagnostics;

namespace Trykatch.Api.Security;

/// <summary>Preserves client-error status without exposing request bodies or CLR details.</summary>
public sealed class RequestBindingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException { StatusCode: >= 400 and < 500 } invalidRequest)
            return false;

        httpContext.Response.StatusCode = invalidRequest.StatusCode;
        await Results.Problem(statusCode: invalidRequest.StatusCode,
            title: "Invalid request",
            detail: "Check the request format, field names and value types.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" })
            .ExecuteAsync(httpContext);
        return true;
    }
}
