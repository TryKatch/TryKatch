using Trykatch.Application.Realtime;

namespace Trykatch.Infrastructure.Realtime;

internal sealed class RealtimeHistoryReader(RealtimeHistoryStore history, IRealtimeAudienceAuthorization authorization)
    : IRealtimeHistoryReader
{
    public Task<long> ReadHeadAsync(CancellationToken cancellationToken = default) => history.ReadHeadAsync(cancellationToken);

    public async Task<RealtimeHistoryBatch> ReadPageAsync(long after, CancellationToken cancellationToken = default)
    {
        RealtimeHistoryPage page = await history.ReadPageAsync(after, cancellationToken);
        List<RealtimeHistoryEntry> visible = [];
        // The store has materialized and disposed its database reader before a policy can query the scoped connection.
        foreach (RealtimeHistoryRecord row in page.Items)
        {
            var notification = row.ToNotification();
            if (await authorization.AuthorizeAsync(notification, cancellationToken))
                visible.Add(new(row.Sequence, notification));
        }
        return new(visible, page.Position, page.HasMore);
    }
}
