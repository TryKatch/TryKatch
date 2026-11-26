using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Connections.Features;
using Trykatch.Modules;

namespace Trykatch.Api.Realtime;

internal sealed class ModuleRealtimeHub(RealtimeConnectionOperations operations, RealtimeConnectionRegistry registry) : Hub
{
    public override async Task OnConnectedAsync()
    {
        HttpContext? context = Context.GetHttpContext();
        IConnectionLifetimeFeature? lifetime = Context.Features.Get<IConnectionLifetimeFeature>();
        if (context is null || lifetime is null
            || !await operations.AdmitAsync(context, Context.ConnectionId, lifetime, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    public Task ReplaceSubscriptions(ModuleRealtimeAudience[] subscriptions) => ReplaceAsync(subscriptions);

    private async Task ReplaceAsync(ModuleRealtimeAudience[] subscriptions)
    {
        if (!await operations.ReplaceAsync(Context.ConnectionId, subscriptions, Context.ConnectionAborted))
            throw new HubException("Realtime subscription replacement was denied.");
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.TryGet(Context.ConnectionId, out RealtimeConnectionEntry? entry) && entry is not null) registry.Remove(entry, false);
        return base.OnDisconnectedAsync(exception);
    }
}
