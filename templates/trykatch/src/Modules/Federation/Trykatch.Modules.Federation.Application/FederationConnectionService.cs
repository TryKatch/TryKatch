using Trykatch.Modules.Federation.Domain;

namespace Trykatch.Modules.Federation.Application;

public sealed class FederationConnectionService(
    IFederationConnectionStore store,
    IFederationProviderProbe providerProbe,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<FederationConnectionDto>> ListAsync(CancellationToken cancellationToken) =>
        (await store.ListAsync(cancellationToken)).Select(ToDto).ToArray();

    public async Task<FederationOperationResult<FederationConnectionDto>> CreateAsync(
        SaveFederationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        FederationOperationResult<NormalizedConnection> validation = await ValidateAsync(request, requireSecret: true, cancellationToken);
        if (validation.Value is null)
            return FederationOperationResult.Failure<FederationConnectionDto>(validation.Code!, validation.Error!);
        NormalizedConnection value = validation.Value;
        FederationConnection? created = await store.CreateAsync(
            Guid.NewGuid(), value.Name, value.Issuer, value.ClientId, value.ClientSecret!, cancellationToken);
        return created is null
            ? FederationOperationResult.Failure<FederationConnectionDto>("conflict", "That issuer and client ID are already configured.")
            : FederationOperationResult.Success(ToDto(created));
    }

    public async Task<FederationOperationResult<FederationConnectionDto>> UpdateAsync(
        Guid id,
        SaveFederationConnectionRequest request,
        CancellationToken cancellationToken)
    {
        StoredFederationConnection? current = await store.GetAsync(id, cancellationToken);
        if (current is null)
            return FederationOperationResult.Failure<FederationConnectionDto>("not_found", "The SSO connection was not found.");
        FederationOperationResult<NormalizedConnection> validation = await ValidateAsync(request, requireSecret: false, cancellationToken);
        if (validation.Value is null)
            return FederationOperationResult.Failure<FederationConnectionDto>(validation.Code!, validation.Error!);
        NormalizedConnection value = validation.Value;
        FederationConnection? updated = await store.UpdateAsync(
            current, value.Name, value.Issuer, value.ClientId, value.ClientSecret, cancellationToken);
        return updated is null
            ? FederationOperationResult.Failure<FederationConnectionDto>("conflict", "That issuer and client ID are already configured.")
            : FederationOperationResult.Success(ToDto(updated));
    }

    public async Task<FederationOperationResult<FederationTestResult>> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        StoredFederationConnection? connection = await store.GetAsync(id, cancellationToken);
        if (connection is null)
            return FederationOperationResult.Failure<FederationTestResult>("not_found", "The SSO connection was not found.");

        DateTimeOffset testedAt = timeProvider.GetUtcNow();
        FederationProviderProbeResult probe = await providerProbe.TestDiscoveryAsync(
            connection.Public.Issuer,
            cancellationToken);
        await store.MarkTestedAsync(connection, probe.IsSuccess, probe.Message, cancellationToken);
        return probe.IsSuccess
            ? FederationOperationResult.Success(new FederationTestResult(true, probe.Message, testedAt))
            : FederationOperationResult.Failure<FederationTestResult>("provider_unavailable", probe.Message);
    }

    public async Task<FederationOperationResult<FederationConnectionDto>> SetEnabledAsync(
        Guid id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        StoredFederationConnection? current = await store.GetAsync(id, cancellationToken);
        if (current is null)
            return FederationOperationResult.Failure<FederationConnectionDto>("not_found", "The SSO connection was not found.");
        if (!await store.SetEnabledAsync(current, enabled, cancellationToken))
            return FederationOperationResult.Failure<FederationConnectionDto>(
                "not_tested",
                "The current configuration must pass a provider test before it can be enabled.");
        return FederationOperationResult.Success(ToDto((await store.GetAsync(id, cancellationToken))!.Public));
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
            FederationProviderProbeResult issuer = await providerProbe.ValidateIssuerAsync(request.Issuer, cancellationToken);
            if (!issuer.IsSuccess || issuer.NormalizedIssuer is null)
                return FederationOperationResult.Failure<NormalizedConnection>("validation", issuer.Message);
            return FederationOperationResult.Success<NormalizedConnection>(new(
                name,
                issuer.NormalizedIssuer,
                clientId,
                string.IsNullOrWhiteSpace(request.ClientSecret) ? null : request.ClientSecret));
        }
        catch (InvalidOperationException exception)
        {
            return FederationOperationResult.Failure<NormalizedConnection>("validation", exception.Message);
        }
    }

    private sealed record NormalizedConnection(string Name, string Issuer, string ClientId, string? ClientSecret);

    private static FederationConnectionDto ToDto(FederationConnection connection) => new(
        connection.Id,
        connection.Name,
        connection.Issuer,
        connection.ClientId,
        connection.SecretConfigured,
        connection.Enabled,
        connection.LastTestedAt,
        connection.LastTestResult,
        connection.UpdatedAt);
}

public sealed record SaveFederationConnectionRequest(
    string Name,
    string Issuer,
    string ClientId,
    string? ClientSecret);

public sealed record FederationConnectionDto(
    Guid Id,
    string Name,
    string Issuer,
    string ClientId,
    bool SecretConfigured,
    bool Enabled,
    DateTimeOffset? LastTestedAt,
    string? LastTestResult,
    DateTimeOffset UpdatedAt);

public sealed record FederationTestResult(bool Successful, string Message, DateTimeOffset TestedAt);

public sealed record FederationProviderProbeResult(bool IsSuccess, string? NormalizedIssuer, string Message);

/// <summary>External OpenID Connect validation seam implemented by Infrastructure.</summary>
public interface IFederationProviderProbe
{
    Task<FederationProviderProbeResult> ValidateIssuerAsync(string issuer, CancellationToken cancellationToken);
    Task<FederationProviderProbeResult> TestDiscoveryAsync(string normalizedIssuer, CancellationToken cancellationToken);
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
