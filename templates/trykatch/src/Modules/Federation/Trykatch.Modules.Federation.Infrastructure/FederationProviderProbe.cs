using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trykatch.Modules.Federation.Application;

namespace Trykatch.Modules.Federation.Infrastructure;

internal sealed class FederationProviderProbe(
    IHttpClientFactory httpClientFactory,
    IOptions<FederationSecurityOptions> securityOptions) : IFederationProviderProbe
{
    private const int MaximumDiscoveryBytes = 256 * 1024;
    private static readonly TimeSpan DiscoveryBodyTimeout = TimeSpan.FromSeconds(10);

    public async Task<FederationProviderProbeResult> ValidateIssuerAsync(
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            Uri issuer = await ValidateIssuerCoreAsync(value, cancellationToken);
            return new(true, issuer.AbsoluteUri.TrimEnd('/'), string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SocketException)
        {
            return new(false, null, exception.Message);
        }
    }

    public async Task<FederationProviderProbeResult> TestDiscoveryAsync(
        string normalizedIssuer,
        CancellationToken cancellationToken)
    {
        try
        {
            Uri issuer = await ValidateIssuerCoreAsync(normalizedIssuer, cancellationToken);
            Uri discovery = new(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
            using HttpRequestMessage request = new(HttpMethod.Get, discovery);
            using HttpResponseMessage response = await httpClientFactory.CreateClient("trykatch-federation-discovery")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await ReadBoundedDiscoveryAsync(response.Content, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(bytes);
            string discoveredIssuer = RequiredAbsoluteUri(document.RootElement, "issuer").AbsoluteUri.TrimEnd('/');
            if (!string.Equals(discoveredIssuer, issuer.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
                throw new InvalidOperationException("Discovery issuer does not exactly match the configured issuer.");
            _ = RequiredAbsoluteUri(document.RootElement, "authorization_endpoint");
            _ = RequiredAbsoluteUri(document.RootElement, "token_endpoint");
            _ = RequiredAbsoluteUri(document.RootElement, "jwks_uri");
            return new(true, issuer.AbsoluteUri.TrimEnd('/'), "OIDC discovery and issuer binding passed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or SocketException)
        {
            return new(false, null, $"Provider test failed: {exception.Message}");
        }
    }

    private async Task<Uri> ValidateIssuerCoreAsync(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? issuer)
            || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment))
            throw new InvalidOperationException("Issuer must be an absolute URL without a query or fragment.");
        bool insecureLoopback = issuer.Scheme == Uri.UriSchemeHttp
            && securityOptions.Value.AllowInsecureLoopbackIssuer
            && (issuer.IsLoopback || string.Equals(issuer.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase));
        if (issuer.Scheme != Uri.UriSchemeHttps && !insecureLoopback)
            throw new InvalidOperationException("Issuer must use HTTPS. Only an explicit local loopback exception is supported.");

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(issuer.DnsSafeHost, cancellationToken);
        FederationNetworkGuard.EnsureAddressesAreAllowed(addresses, insecureLoopback);
        return issuer;
    }

    internal static async Task<byte[]> ReadBoundedDiscoveryAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumDiscoveryBytes)
            throw new InvalidOperationException("Discovery response exceeds the 256 KiB safety limit.");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DiscoveryBodyTimeout);
        try
        {
            await using Stream input = await content.ReadAsStreamAsync(deadline.Token);
            using MemoryStream output = new(capacity: 16 * 1024);
            byte[] buffer = new byte[8 * 1024];
            while (true)
            {
                int read = await input.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > MaximumDiscoveryBytes)
                    throw new InvalidOperationException("Discovery response exceeds the 256 KiB safety limit.");
                output.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Discovery response body exceeded the 10-second safety deadline.");
        }
    }

    private static Uri RequiredAbsoluteUri(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"Discovery document is missing a valid '{property}'.");
        return uri;
    }
}

internal static class FederationNetworkGuard
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    public static SocketsHttpHandler CreateHandler(bool allowInsecureLoopbackIssuer) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = Timeout.InfiniteTimeSpan,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, cancellationToken) =>
            ConnectAsync(context, allowInsecureLoopbackIssuer, cancellationToken)
    };

    public static void EnsureAddressesAreAllowed(IEnumerable<IPAddress> addresses, bool allowPrivateAddresses)
    {
        IPAddress[] resolved = addresses.ToArray();
        if (resolved.Length == 0)
            throw new InvalidOperationException("Issuer did not resolve to a network address.");
        if (!allowPrivateAddresses && resolved.Any(IsPrivateOrMetadataAddress))
            throw new InvalidOperationException("Issuer resolves to a private, loopback, link-local, or metadata network address.");
    }

    internal static bool IsPrivateOrMetadataAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPrivateOrMetadataAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] ipv6 = address.GetAddressBytes();
            return (ipv6[0] & 0xfe) == 0xfc; // RFC 4193 unique-local fc00::/7.
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            || bytes[0] >= 224;
    }

    internal static bool IsExplicitLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        bool allowInsecureLoopbackIssuer,
        CancellationToken cancellationToken)
    {
        bool explicitLoopbackException = allowInsecureLoopbackIssuer
            && IsExplicitLoopbackHost(context.DnsEndPoint.Host);
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        EnsureAddressesAreAllowed(addresses, explicitLoopbackException);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectionTimeout);
        List<Task<Socket>> attempts = addresses
            .Distinct()
            .Select(address => ConnectAddressAsync(address, context.DnsEndPoint.Port, deadline.Token))
            .ToList();
        Exception? lastFailure = null;
        while (attempts.Count > 0)
        {
            Task<Socket> completed = await Task.WhenAny(attempts);
            attempts.Remove(completed);
            try
            {
                Socket connected = await completed;
                deadline.Cancel();
                foreach (Task<Socket> remaining in attempts)
                    await DisposeConnectionResultAsync(remaining);
                return new NetworkStream(connected, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                lastFailure = exception;
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        throw new SocketException((lastFailure as SocketException)?.ErrorCode ?? (int)SocketError.TimedOut);
    }

    private static async Task<Socket> ConnectAddressAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task DisposeConnectionResultAsync(Task<Socket> attempt)
    {
        try
        {
            (await attempt).Dispose();
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            // Failed and cancelled attempts dispose their own sockets.
        }
    }
}

internal sealed class FederationSecurityOptions
{
    public bool AllowInsecureLoopbackIssuer { get; init; }
}
