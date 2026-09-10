using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Trykatch.Modules.Federation.Domain;

namespace Trykatch.Modules.Federation.Application;

public sealed class FederationConnectionService(
    IFederationConnectionStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<FederationSecurityOptions> securityOptions,
    TimeProvider timeProvider)
{
    private const int MaximumDiscoveryBytes = 256 * 1024;

    public async Task<FederationOperationResult<FederationConnection>> CreateAsync(
        SaveFederationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        FederationOperationResult<NormalizedConnection> validation = await ValidateAsync(request, requireSecret: true, cancellationToken);
        if (validation.Value is null)
            return FederationOperationResult.Failure<FederationConnection>(validation.Code!, validation.Error!);
        NormalizedConnection value = validation.Value;
        FederationConnection? created = await store.CreateAsync(
            Guid.NewGuid(), value.Name, value.Issuer, value.ClientId, value.ClientSecret!, cancellationToken);
        return created is null
            ? FederationOperationResult.Failure<FederationConnection>("conflict", "That issuer and client ID are already configured.")
            : FederationOperationResult.Success<FederationConnection>(created);
    }

    public async Task<FederationOperationResult<FederationConnection>> UpdateAsync(
        Guid id,
        SaveFederationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        StoredFederationConnection? current = await store.GetAsync(id, cancellationToken);
        if (current is null)
            return FederationOperationResult.Failure<FederationConnection>("not_found", "The SSO connection was not found.");
        FederationOperationResult<NormalizedConnection> validation = await ValidateAsync(request, requireSecret: false, cancellationToken);
        if (validation.Value is null)
            return FederationOperationResult.Failure<FederationConnection>(validation.Code!, validation.Error!);
        NormalizedConnection value = validation.Value;
        FederationConnection? updated = await store.UpdateAsync(
            current, value.Name, value.Issuer, value.ClientId, value.ClientSecret, cancellationToken);
        return updated is null
            ? FederationOperationResult.Failure<FederationConnection>("conflict", "That issuer and client ID are already configured.")
            : FederationOperationResult.Success<FederationConnection>(updated);
    }

    public async Task<FederationOperationResult<FederationTestResult>> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        StoredFederationConnection? connection = await store.GetAsync(id, cancellationToken);
        if (connection is null)
            return FederationOperationResult.Failure<FederationTestResult>("not_found", "The SSO connection was not found.");

        DateTimeOffset testedAt = timeProvider.GetUtcNow();
        try
        {
            Uri issuer = await ValidateIssuerAsync(connection.Public.Issuer, cancellationToken);
            Uri discovery = new(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
            using HttpRequestMessage request = new(HttpMethod.Get, discovery);
            using HttpResponseMessage response = await httpClientFactory.CreateClient("trykatch-federation-discovery")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaximumDiscoveryBytes)
                throw new InvalidOperationException("Discovery response exceeds the 256 KiB safety limit.");
            using JsonDocument document = JsonDocument.Parse(bytes);
            string discoveredIssuer = RequiredAbsoluteUri(document.RootElement, "issuer").AbsoluteUri.TrimEnd('/');
            if (!string.Equals(discoveredIssuer, issuer.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
                throw new InvalidOperationException("Discovery issuer does not exactly match the configured issuer.");
            _ = RequiredAbsoluteUri(document.RootElement, "authorization_endpoint");
            _ = RequiredAbsoluteUri(document.RootElement, "token_endpoint");
            _ = RequiredAbsoluteUri(document.RootElement, "jwks_uri");
            const string message = "OIDC discovery and issuer binding passed.";
            await store.MarkTestedAsync(connection, true, message, cancellationToken);
            return FederationOperationResult.Success<FederationTestResult>(new(true, message, testedAt));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or SocketException)
        {
            string message = $"Provider test failed: {exception.Message}";
            await store.MarkTestedAsync(connection, false, message, cancellationToken);
            return FederationOperationResult.Failure<FederationTestResult>("provider_unavailable", message);
        }
    }

    public async Task<FederationOperationResult<FederationConnection>> SetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        StoredFederationConnection? current = await store.GetAsync(id, cancellationToken);
        if (current is null)
            return FederationOperationResult.Failure<FederationConnection>("not_found", "The SSO connection was not found.");
        if (!await store.SetEnabledAsync(current, enabled, cancellationToken))
            return FederationOperationResult.Failure<FederationConnection>(
                "not_tested",
                "The current configuration must pass a provider test before it can be enabled.");
        return FederationOperationResult.Success<FederationConnection>((await store.GetAsync(id, cancellationToken))!.Public);
    }

    public async Task<FederationOperationResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        StoredFederationConnection? current = await store.GetAsync(id, cancellationToken);
        if (current is null)
            return FederationOperationResult.Failure<bool>("not_found", "The SSO connection was not found.");
        return await store.DeleteAsync(current, cancellationToken)
            ? FederationOperationResult.Success<bool>(true)
            : FederationOperationResult.Failure<bool>("conflict", "Disable the SSO connection before deleting it.");
    }

    private async Task<FederationOperationResult<NormalizedConnection>> ValidateAsync(
        SaveFederationConnectionRequest request,
        bool requireSecret,
        CancellationToken cancellationToken)
    {
        string name = request.Name.Trim();
        string clientId = request.ClientId.Trim();
        if (name.Length is < 2 or > 120 || clientId.Length is < 2 or > 240)
            return FederationOperationResult.Failure<NormalizedConnection>("validation", "Name and client ID are required and exceed no field limits.");
        if (requireSecret && string.IsNullOrWhiteSpace(request.ClientSecret))
            return FederationOperationResult.Failure<NormalizedConnection>("validation", "A client secret is required.");
        if (request.ClientSecret?.Length > 2048)
            return FederationOperationResult.Failure<NormalizedConnection>("validation", "The client secret exceeds the supported length.");
        try
        {
            Uri issuer = await ValidateIssuerAsync(request.Issuer, cancellationToken);
            return FederationOperationResult.Success<NormalizedConnection>(new(
                name,
                issuer.AbsoluteUri.TrimEnd('/'),
                clientId,
                string.IsNullOrWhiteSpace(request.ClientSecret) ? null : request.ClientSecret));
        }
        catch (InvalidOperationException exception)
        {
            return FederationOperationResult.Failure<NormalizedConnection>("validation", exception.Message);
        }
    }

    private async Task<Uri> ValidateIssuerAsync(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? issuer) || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment))
            throw new InvalidOperationException("Issuer must be an absolute URL without a query or fragment.");
        bool insecureLoopback = issuer.Scheme == Uri.UriSchemeHttp
            && securityOptions.Value.AllowInsecureLoopbackIssuer
            && (issuer.IsLoopback || string.Equals(issuer.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase));
        if (issuer.Scheme != Uri.UriSchemeHttps && !insecureLoopback)
            throw new InvalidOperationException("Issuer must use HTTPS. Only an explicit local loopback exception is supported.");

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(issuer.DnsSafeHost, cancellationToken);
        if (!insecureLoopback && addresses.Any(IsPrivateOrMetadataAddress))
            throw new InvalidOperationException("Issuer resolves to a private, loopback, link-local, or metadata network address.");
        return issuer;
    }

    private static bool IsPrivateOrMetadataAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static Uri RequiredAbsoluteUri(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)
            || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException($"Discovery document is missing a valid '{property}'.");
        return uri;
    }

    private sealed record NormalizedConnection(string Name, string Issuer, string ClientId, string? ClientSecret);
}

public sealed class FederationSecurityOptions
{
    public bool AllowInsecureLoopbackIssuer { get; init; }
}

public sealed record StoredFederationConnection(
    FederationConnection Public,
    string? ProtectedClientSecret,
    string? TestedConfigurationHash);

public interface IFederationConnectionStore
{
    Task<IReadOnlyList<FederationConnection>> ListAsync(CancellationToken cancellationToken);
    Task<StoredFederationConnection?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<FederationConnection?> CreateAsync(Guid id, string name, string issuer, string clientId, string clientSecret, CancellationToken cancellationToken);
    Task<FederationConnection?> UpdateAsync(StoredFederationConnection current, string name, string issuer, string clientId, string? clientSecret, CancellationToken cancellationToken);
    Task MarkTestedAsync(StoredFederationConnection current, bool successful, string message, CancellationToken cancellationToken);
    Task<bool> SetEnabledAsync(StoredFederationConnection current, bool enabled, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(StoredFederationConnection current, CancellationToken cancellationToken);
    string UnprotectSecret(StoredFederationConnection connection);
}

public sealed record FederationOperationResult<T>(T? Value, string? Code = null, string? Error = null);

public static class FederationOperationResult
{
    public static FederationOperationResult<T> Success<T>(T value) => new(value);

    public static FederationOperationResult<T> Failure<T>(string code, string error) => new(default, code, error);
}
