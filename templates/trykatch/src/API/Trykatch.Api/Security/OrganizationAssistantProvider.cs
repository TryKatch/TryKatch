using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Trykatch.Application.Organizations;
using Trykatch.Modules.AspNetCore.Assistant;

namespace Trykatch.Api.Security;

internal sealed class OrganizationAssistantProviderPolicy(IOptions<AssistantOptions> options) : IOrganizationAssistantProviderPolicy
{
    public IReadOnlyList<string> AllowedEndpoints => options.Value.AllowedTenantEndpoints.Where(IsSafeBase).Distinct(StringComparer.Ordinal).ToArray();
    public bool IsValid(string provider, string model, string endpoint, bool hasKey, int timeoutMs) =>
        options.Value.AllowTenantConfiguration && timeoutMs is >= 1000 and <= 60_000
        && !string.IsNullOrWhiteSpace(model) && model.Length <= 120 && !model.Any(char.IsControl)
        && (provider == "openai" ? endpoint.Length == 0 && hasKey
            : provider is "deepseek" or "chat-completions" or "ollama" && IsSafeBase(endpoint)
                && AllowedEndpoints.Any(allowed => Canonical(allowed) == Canonical(endpoint))
                && (provider == "ollama" || hasKey)
                && (provider != "ollama" || new Uri(endpoint).AbsolutePath == "/")
                && (provider != "deepseek" || new Uri(endpoint).Host == "api.deepseek.com"));

    private static string Canonical(string endpoint) => new Uri(endpoint).AbsoluteUri.TrimEnd('/');
    private static bool IsSafeBase(string endpoint) => endpoint.Length <= 500 && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Port == 443 && uri.AbsolutePath is "/" or "/v1" or "/v1/";
}

internal sealed class OrganizationAssistantProvider(IOrganizationAssistantSettingStore store, IOrganizationContext context,
    IOrganizationAssistantKeyProtector keys, IOrganizationAssistantProviderPolicy policy,
    IHttpClientFactory clients) : IAssistantTenantProvider
{
    public async Task<bool?> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var setting = await store.FindAsync(context.OrganizationId, cancellationToken);
        if (setting is null || setting.Provider.Length == 0) return null;
        return policy.IsValid(setting.Provider, setting.Model, setting.Endpoint, setting.ProtectedApiKey.Length > 0, setting.TimeoutMs);
    }

    public async Task<AssistantProviderSession?> CreateAsync(CancellationToken cancellationToken)
    {
        var setting = await store.FindAsync(context.OrganizationId, cancellationToken);
        if (setting is null || setting.Provider.Length == 0) return null;
        if (!policy.IsValid(setting.Provider, setting.Model, setting.Endpoint, setting.ProtectedApiKey.Length > 0, setting.TimeoutMs))
            throw new AssistantException("invalid_provider_configuration");
        string key;
        try { key = setting.ProtectedApiKey.Length == 0 ? "" : keys.Unprotect(context.OrganizationId, setting.ProtectedApiKey); }
        catch (CryptographicException) { throw new AssistantException("invalid_provider_configuration"); }
        AssistantOptions configured = new() { Enabled = true, Provider = setting.Provider == "deepseek" ? "chat-completions" : setting.Provider,
            Model = setting.Model, Endpoint = setting.Endpoint, ApiKey = key, TimeoutMs = setting.TimeoutMs };
        return new(configured, AssistantProviders.Create(clients.CreateClient("assistant-provider"), Options.Create(configured)));
    }
}
