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
        if (isOperationalRequest)
        {
            using IDisposable suppression = SuppressInstrumentationScope.Begin();
            await next(context);
        }
        else
        {
            await next(context);
        }

        if (isOperationalRequest && context.Response.StatusCode < 400) return;
        string route = context.GetEndpoint()?.Metadata.GetMetadata<RouteEndpoint>()?.RoutePattern.RawText ?? "unmatched";
        if (route.Length > 160) route = "unmatched";
        LogRequest(logger, context.Request.Method, route, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    [LoggerMessage(5000, LogLevel.Information,
        "HTTP request {RequestMethod} {RouteTemplate} responded {StatusCode} in {ElapsedMilliseconds:0.0000} ms")]
    private static partial void LogRequest(ILogger logger, string requestMethod, string routeTemplate, int statusCode, double elapsedMilliseconds);
}
