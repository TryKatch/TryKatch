using Trykatch.Modules;

namespace Trykatch.Application.Realtime;

public sealed record RealtimeHistoryEntry(long Position, ModuleRealtimeNotification Notification);
public sealed record RealtimeHistoryBatch(IReadOnlyList<RealtimeHistoryEntry> Items, long Position, bool HasMore);

/// <summary>Materialized current-recipient pages in an existing organization read-only transaction.</summary>
public interface IRealtimeHistoryReader
{
    Task<long> ReadHeadAsync(CancellationToken cancellationToken = default);
    Task<RealtimeHistoryBatch> ReadPageAsync(long after, CancellationToken cancellationToken = default);
}

/// <summary>Evaluates installed module policy in a fresh, resolved recipient scope; never a cached authority.</summary>
public interface IRealtimeAudienceAuthorization
{
    Task<bool> AuthorizeAsync(ModuleRealtimeAudience audience, CancellationToken cancellationToken = default);
    Task<bool> AuthorizeAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default);
}
