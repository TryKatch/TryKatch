namespace Trykatch.Api.Security;

public interface IApplicationUrlResolver
{
    string ResolveBaseUrl(HttpRequest request);
}

internal sealed class ApplicationUrlResolver(IWebHostEnvironment environment, IConfiguration configuration) : IApplicationUrlResolver
{
    public string ResolveBaseUrl(HttpRequest request)
    {
        string? configuredUrl = configuration["Application:PublicUrl"]?.TrimEnd('/');
        if (IsHttpUrl(configuredUrl, allowInsecure: environment.IsDevelopment()))
        {
            return configuredUrl!;
        }

        string? origin = request.Headers.Origin.FirstOrDefault()?.TrimEnd('/');
        if (environment.IsDevelopment()
            && Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
            && originUri.IsLoopback
            && IsHttpUrl(origin, allowInsecure: true))
        {
            return origin!;
        }

        return $"{request.Scheme}://{request.Host}";
    }

    private static bool IsHttpUrl(string? value, bool allowInsecure)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (allowInsecure && uri.Scheme == Uri.UriSchemeHttp));
    }
}
