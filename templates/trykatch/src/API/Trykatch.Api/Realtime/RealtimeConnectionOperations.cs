using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using Trykatch.Api.Security;
using Trykatch.Application.Identity;
using Trykatch.Application.Realtime;
using Trykatch.Modules;

namespace Trykatch.Api.Realtime;

internal sealed class RealtimeConnectionOperations(
    IServiceScopeFactory scopes, RealtimeConnectionRegistry registry, IOptions<RealtimeOptions> options,
    IHubContext<ModuleRealtimeHub> hub) : IDisposable
{
    private readonly SemaphoreSlim attempts = new(options.Value.MaximumConcurrentSends);

    internal async Task<bool> AdmitAsync(HttpContext context, string connectionId, IConnectionLifetimeFeature lifetime,
        CancellationToken cancellationToken)
    {
        AccountSessionProof? proof = context.RequestServices.GetRequiredService<ICurrentAccountSession>().Proof;
        if (proof is null || !context.RequestServices.GetRequiredService<IWorkspaceContextCookie>().TryRead(context, out Guid organizationId)
            || registry.Count >= options.Value.MaximumConnections || !await attempts.WaitAsync(0, cancellationToken))
        {
            RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Rejected);
            return false;
        }
        try
        {
            using CancellationTokenSource deadline = Deadline(cancellationToken);
            (RealtimeConnectionBinding Binding, long Head)? admission = await ReadAdmissionAsync(proof, organizationId, deadline.Token);
            bool admitted = admission is { } value && registry.TryAdd(connectionId, value.Binding, lifetime, value.Head, out _);
            RealtimeMetrics.Record(options.Value.Mode, admitted ? RealtimeOutcome.Admitted : RealtimeOutcome.Rejected);
            return admitted;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Failed);
            return false;
        }
        finally { attempts.Release(); }
    }

    private async Task<(RealtimeConnectionBinding Binding, long Head)?> ReadAdmissionAsync(
        AccountSessionProof proof, Guid organizationId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AccountSessionState? state = await scope.ServiceProvider.GetRequiredService<IAccountSessions>().ValidateAsync(proof, cancellationToken);
        if (state is null || state.OrganizationId != organizationId) return null;
        RealtimeConnectionBinding binding = new(proof, organizationId, state.WorkspaceGeneration);
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        if (!await authority.OpenAsync(binding, cancellationToken)) return null;
        long head = await scope.ServiceProvider.GetRequiredService<IRealtimeHistoryReader>().ReadHeadAsync(cancellationToken);
        return await authority.IsSessionCurrentAsync(binding, cancellationToken) ? (binding, head) : null;
    }

    internal async Task<bool> ReplaceAsync(string connectionId, IReadOnlyList<ModuleRealtimeAudience>? subscriptions,
        CancellationToken cancellationToken)
    {
        if (subscriptions is null || subscriptions.Count > options.Value.MaximumSubscriptions
            || subscriptions.Distinct().Count() != subscriptions.Count
            || !registry.TryGet(connectionId, out RealtimeConnectionEntry? entry) || entry is null
            || !await attempts.WaitAsync(0, cancellationToken)) return false;
        try
        {
            using CancellationTokenSource deadline = Deadline(cancellationToken);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
            if (!await authority.OpenAsync(entry.Binding, deadline.Token))
            {
                registry.Remove(entry, true);
                return false;
            }
            IRealtimeAudienceAuthorization authorization = scope.ServiceProvider.GetRequiredService<IRealtimeAudienceAuthorization>();
            foreach (ModuleRealtimeAudience subscription in subscriptions)
                if (!await authorization.AuthorizeAsync(subscription, deadline.Token)) return false;
            if (!await authority.IsSessionCurrentAsync(entry.Binding, deadline.Token))
            {
                registry.Remove(entry, true);
                return false;
            }
            return entry.TryReplace(subscriptions);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            registry.Remove(entry, true);
            return false;
        }
        finally { attempts.Release(); }
    }

    internal Task RunOnceAsync(CancellationToken cancellationToken) => Parallel.ForEachAsync(registry.Snapshot(),
        new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaximumConcurrentSends, CancellationToken = cancellationToken },
        async (entry, token) => await PollAsync(entry, token));

    private async Task PollAsync(RealtimeConnectionEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.TryBegin()) return;
        bool acquired = false;
        try
        {
            if (!entry.HasTransport) { registry.Remove(entry, true); return; }
            acquired = await attempts.WaitAsync(0, cancellationToken);
            if (!acquired) return;
            var scan = await ReadBatchAsync(entry, cancellationToken);
            if (scan is null) return;
            var (page, version, subscriptions) = scan.Value;
            // One materialized page per poll; there is no queue of previously authorized payloads.
            foreach (RealtimeHistoryEntry item in page.Items)
            {
                if (!entry.IsCurrent(version)) return;
                if (!subscriptions.Any(subscription => Matches(subscription, item.Notification.Audience)))
                {
                    entry.Advance(item.Position);
                    continue;
                }
                if (JsonSerializer.SerializeToUtf8Bytes(item.Notification).Length > RealtimeOptions.MaximumNotificationBytes)
                    throw new InvalidOperationException("Realtime invalidation exceeds the host bound.");
                bool allowed = await AuthorizeSendAsync(entry, subscriptions, item.Notification, cancellationToken);
                if (!entry.IsCurrent(version)) return;
                if (!allowed)
                {
                    RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Suppressed);
                    entry.Advance(item.Position);
                    continue;
                }
                // All scoped authority/resource work has finished and its transaction is disposed before transport I/O.
                using CancellationTokenSource sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendDeadline.CancelAfter(options.Value.SendTimeout);
                using CancellationTokenRegistration abort = sendDeadline.Token.Register(() => registry.Remove(entry, true));
                sendDeadline.Token.ThrowIfCancellationRequested();
                await hub.Clients.Client(entry.ConnectionId).SendAsync("invalidate", item.Notification, sendDeadline.Token);
                sendDeadline.Token.ThrowIfCancellationRequested();
                RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Sent);
                entry.Advance(item.Position);
            }
            if (entry.IsCurrent(version)) entry.Advance(page.Position);
        }
        catch (Exception)
        {
            RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Failed);
            registry.Remove(entry, true);
        }
        finally
        {
            if (acquired) attempts.Release();
            entry.End();
        }
    }

    private async Task<(RealtimeHistoryBatch Page, long Version, IReadOnlyList<ModuleRealtimeAudience> Subscriptions)?>
        ReadBatchAsync(RealtimeConnectionEntry entry, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = Deadline(cancellationToken);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        if (!await authority.OpenAsync(entry.Binding, deadline.Token)) { registry.Remove(entry, true); return null; }
        (long version, IReadOnlyList<ModuleRealtimeAudience> subscriptions) = entry.CaptureSubscriptions();
        IRealtimeAudienceAuthorization authorization = scope.ServiceProvider.GetRequiredService<IRealtimeAudienceAuthorization>();
        foreach (ModuleRealtimeAudience subscription in subscriptions)
            if (!await authorization.AuthorizeAsync(subscription, deadline.Token)) { registry.Remove(entry, true); return null; }
        if (!await authority.IsSessionCurrentAsync(entry.Binding, deadline.Token)) { registry.Remove(entry, true); return null; }
        if (subscriptions.Count == 0 || !entry.IsCurrent(version)) return null;
        RealtimeHistoryBatch page = await scope.ServiceProvider.GetRequiredService<IRealtimeHistoryReader>()
            .ReadPageAsync(entry.Position, deadline.Token);
        return (page, version, subscriptions);
    }

    private async Task<bool> AuthorizeSendAsync(RealtimeConnectionEntry entry, IReadOnlyList<ModuleRealtimeAudience> subscriptions,
        ModuleRealtimeNotification notification, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = Deadline(cancellationToken);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        if (!await authority.OpenAsync(entry.Binding, deadline.Token)) { registry.Remove(entry, true); return false; }
        IRealtimeAudienceAuthorization authorization = scope.ServiceProvider.GetRequiredService<IRealtimeAudienceAuthorization>();
        foreach (ModuleRealtimeAudience subscription in subscriptions)
            if (!await authorization.AuthorizeAsync(subscription, deadline.Token)) { registry.Remove(entry, true); return false; }
        bool allowed = await authorization.AuthorizeAsync(notification, deadline.Token);
        if (!await authority.IsSessionCurrentAsync(entry.Binding, deadline.Token)) { registry.Remove(entry, true); return false; }
        return allowed;
    }

    private static bool Matches(ModuleRealtimeAudience subscription, ModuleRealtimeAudience audience) =>
        subscription.Policy == audience.Policy && subscription.Kind == audience.Kind && subscription.UserId == audience.UserId
        && (subscription.Resource is null || subscription.Resource == audience.Resource);

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(options.Value.AuthorityTimeout);
        return result;
    }

    public void Dispose() => attempts.Dispose();
}

internal sealed class RealtimeRelay(RealtimeConnectionOperations operations, RealtimeConnectionRegistry registry,
    IOptions<RealtimeOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await operations.RunOnceAsync(stoppingToken);
        }
        finally
        {
            foreach (RealtimeConnectionEntry entry in registry.Snapshot()) registry.Remove(entry, true);
        }
    }
}
