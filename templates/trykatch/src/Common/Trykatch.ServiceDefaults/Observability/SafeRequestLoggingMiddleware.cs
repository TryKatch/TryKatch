using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Trykatch.ServiceDefaults.Observability;

internal sealed partial class SafeRequestLoggingMiddleware(RequestDelegate next, ILogger<SafeRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();
        bool isOperationalRequest = OperationalRoutePolicy.IsOperationalPath(context.Request.Path);
        string outcome = "completed";
        int statusCode = context.Response.StatusCode;
        try
        {
            if (isOperationalRequest)
            {
                using IDisposable suppression = SuppressInstrumentationScope.Begin();
                await next(context);
            }
            else
            {
                await next(context);
            }
        }
        catch
        {
            outcome = "failed";
            statusCode = context.Response.HasStarted ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                outcome = "client_aborted";
                // Diagnostic status only; do not mutate a response that may already have started.
                statusCode = 499;
            }
            else if (outcome == "completed")
            {
                statusCode = context.Response.StatusCode;
            }

            if (!isOperationalRequest || outcome != "completed" || statusCode >= 400)
            {
                string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                if (route.Length > 160) route = "unmatched";
                string method = context.Request.Method switch
                {
                    "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" or "TRACE" or "CONNECT" => context.Request.Method,
                    _ => "OTHER"
                };
                LogRequest(logger, method, route, statusCode, outcome, context.Response.HasStarted, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    [LoggerMessage(5000, LogLevel.Information,
        "HTTP request {RequestMethod} {RouteTemplate} completed with {Outcome}, diagnostic status {StatusCode}, response started {ResponseStarted}, in {ElapsedMilliseconds:0.0000} ms")]
    private static partial void LogRequest(ILogger logger, string requestMethod, string routeTemplate, int statusCode, string outcome, bool responseStarted, double elapsedMilliseconds);
}
