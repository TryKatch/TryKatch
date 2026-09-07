using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;

namespace FlatpackApp.Api.Security;

public sealed class AntiforgeryExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AntiforgeryValidationException) return false;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Request verification failed",
            detail: "Refresh the page and try again.")
            .ExecuteAsync(httpContext);
        return true;
    }
}
