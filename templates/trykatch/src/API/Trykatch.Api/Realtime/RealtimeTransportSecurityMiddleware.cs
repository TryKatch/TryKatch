using Microsoft.AspNetCore.Antiforgery;

namespace Trykatch.Api.Realtime;

internal sealed class RealtimeTransportMetadata;

internal sealed class RealtimeOriginPolicy
{
    private readonly string? publicOrigin;

    public RealtimeOriginPolicy(IConfiguration configuration)
    {
        string? configured = configuration["Application:PublicUrl"];
        if (configured is null) return;
        if (!TryOrigin(configured, false, out string? origin))
            throw new InvalidOperationException("Realtime requires a valid HTTP application public URL.");
        publicOrigin = origin;
    }

    internal bool Allows(HttpRequest request)
    {
        var origins = request.Headers.Origin;
        return origins.Count == 1 && TryOrigin(origins[0], true, out string? origin)
            && string.Equals(origin, publicOrigin ?? request.Scheme + "://" + request.Host,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOrigin(string? value, bool originHeader, out string? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        string authority = uri.GetLeftPart(UriPartial.Authority);
        if (originHeader && !string.Equals(value, authority, StringComparison.OrdinalIgnoreCase)) return false;
        origin = authority;
        return true;
    }
}

internal sealed class RealtimeTransportSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RealtimeOriginPolicy origin, IAntiforgery antiforgery)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RealtimeTransportMetadata>() is null)
        {
            await next(context);
            return;
        }
        if (context.Request.Query.ContainsKey("access_token"))
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "realtime_request_invalid").ExecuteAsync(context);
            return;
        }
        if (!origin.Allows(context.Request))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "realtime_origin_denied").ExecuteAsync(context);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            await antiforgery.ValidateRequestAsync(context);
        await next(context);
    }
}
