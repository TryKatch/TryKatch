using Microsoft.Extensions.AI;

namespace Trykatch.Modules.AspNetCore.Assistant;

/// <summary>Null means no tenant override. A configured but unusable override never falls back.</summary>
public interface IAssistantTenantProvider
{
    Task<bool?> IsAvailableAsync(CancellationToken cancellationToken);
    Task<AssistantProviderSession?> CreateAsync(CancellationToken cancellationToken);
}

public sealed class AssistantProviderSession(AssistantOptions options, IChatClient client) : IDisposable
{
    public AssistantOptions Options { get; } = options;
    public IChatClient Client { get; } = client;
    public void Dispose() => Client.Dispose();
}
