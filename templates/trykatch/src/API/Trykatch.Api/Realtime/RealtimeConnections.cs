using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Options;
using Trykatch.Application.Identity;
using Trykatch.Modules;

namespace Trykatch.Api.Realtime;

internal sealed record RealtimeConnectionBinding(AccountSessionProof Proof, Guid OrganizationId, Guid Generation)
{
    public override string ToString() => "Realtime connection binding";
}

internal sealed class RealtimeConnectionEntry
{
    private readonly object sync = new();
    private readonly WeakReference<IConnectionLifetimeFeature> lifetime;
    private readonly int maximumSubscriptions;
    private IReadOnlyList<ModuleRealtimeAudience> subscriptions = Array.AsReadOnly(Array.Empty<ModuleRealtimeAudience>());
    private long version;
    private long position;
    private bool closed;
    private bool sending;
    private bool aborted;

    internal RealtimeConnectionEntry(string connectionId, RealtimeConnectionBinding binding,
        IConnectionLifetimeFeature lifetime, long position, int maximumSubscriptions)
    {
        ConnectionId = connectionId;
        Binding = binding;
        this.lifetime = new(lifetime);
        this.position = position;
        this.maximumSubscriptions = maximumSubscriptions;
    }

    internal string ConnectionId { get; }
    internal RealtimeConnectionBinding Binding { get; }
    internal long Position { get { lock (sync) return position; } }
    internal bool HasTransport => lifetime.TryGetTarget(out _);

    internal (long Version, IReadOnlyList<ModuleRealtimeAudience> Items) CaptureSubscriptions()
    {
        lock (sync) return (version, subscriptions);
    }

    internal bool TryReplace(IReadOnlyList<ModuleRealtimeAudience> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumSubscriptions) return false;
        ModuleRealtimeAudience[] copy = values.ToArray();
        lock (sync)
        {
            if (closed) return false;
            subscriptions = Array.AsReadOnly(copy);
            version++;
            return true;
        }
    }

    internal bool IsCurrent(long expectedVersion)
    {
        lock (sync) return !closed && version == expectedVersion;
    }

    internal bool TryBegin()
    {
        lock (sync)
        {
            if (closed || sending) return false;
            sending = true;
            return true;
        }
    }

    internal void End() { lock (sync) sending = false; }

    internal void Advance(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        lock (sync) position = Math.Max(position, value);
    }

    internal void Close(bool abort)
    {
        lock (sync)
        {
            closed = true;
            subscriptions = Array.AsReadOnly(Array.Empty<ModuleRealtimeAudience>());
            if (!abort || aborted) return;
            aborted = true;
        }
        // The framework owns the active transport. A strong feature/delegate/token would retain its request graph.
        if (lifetime.TryGetTarget(out IConnectionLifetimeFeature? target)) target.Abort();
    }
}

internal sealed class RealtimeConnectionRegistry(IOptions<RealtimeOptions> options)
{
    private readonly object sync = new();
    private readonly Dictionary<string, RealtimeConnectionEntry> entries = new(StringComparer.Ordinal);

    internal int Count { get { lock (sync) return entries.Count; } }

    internal bool TryAdd(string connectionId, RealtimeConnectionBinding binding, IConnectionLifetimeFeature lifetime,
        long position, out RealtimeConnectionEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        entry = null;
        if (connectionId.Length > 256) return false;
        lock (sync)
        {
            if (entries.Count >= options.Value.MaximumConnections || entries.ContainsKey(connectionId)) return false;
            entry = new(connectionId, binding, lifetime, position, options.Value.MaximumSubscriptions);
            entries.Add(connectionId, entry);
            return true;
        }
    }

    internal bool TryGet(string connectionId, out RealtimeConnectionEntry? entry)
    {
        lock (sync) return entries.TryGetValue(connectionId, out entry);
    }

    internal RealtimeConnectionEntry[] Snapshot()
    {
        lock (sync) return entries.Values.ToArray();
    }

    internal void Remove(RealtimeConnectionEntry entry, bool abort)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(entry.ConnectionId, out RealtimeConnectionEntry? current)
                || !ReferenceEquals(current, entry)) return;
            entries.Remove(entry.ConnectionId);
        }
        // Quarantine before Abort, which may synchronously notify the connection's disconnect path.
        RealtimeMetrics.Record(options.Value.Mode, RealtimeOutcome.Closed);
        entry.Close(abort);
    }
}
