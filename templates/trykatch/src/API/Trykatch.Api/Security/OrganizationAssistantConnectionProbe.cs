using Microsoft.Extensions.AI;
using Trykatch.Application.Organizations;
using Trykatch.Modules.AspNetCore.Assistant;

namespace Trykatch.Api.Security;

internal sealed class OrganizationAssistantConnectionProbe(IAssistantTenantProvider provider) : IOrganizationAssistantConnectionProbe
{
    public async Task<bool> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using AssistantProviderSession? session = await provider.CreateAsync(cancellationToken);
            if (session is null) return false;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(session.Options.TimeoutMs, 10_000)));
            // Explicit user-triggered billable smoke test; no business data or tools.
            ChatResponse response = await session.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with OK.")],
                new ChatOptions { ModelId = session.Options.Model, MaxOutputTokens = 8 }, deadline.Token);
            return !string.IsNullOrWhiteSpace(response.Text) && !response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any();
        }
        catch (Exception exception) when (exception is AssistantException or HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}
