using System.Data.Common;

namespace Trykatch.Infrastructure.Caching;

/// <summary>Scoped, cache-only work that may run after the owning transaction commits.</summary>
internal sealed class CacheInvalidationCommitState : IDisposable
{
    private static readonly TimeSpan RemovalTimeout = TimeSpan.FromSeconds(2);
    private readonly Dictionary<DbTransaction, Dictionary<string, Func<CancellationToken, ValueTask>>> pending =
        new(ReferenceEqualityComparer.Instance);
    private bool disposed;

    public void Register(DbTransaction transaction, string physicalKey, Func<CancellationToken, ValueTask> remove)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(physicalKey);
        ArgumentNullException.ThrowIfNull(remove);
        if (!pending.TryGetValue(transaction, out var removals))
        {
            removals = new(StringComparer.Ordinal);
            pending.Add(transaction, removals);
        }
        removals.TryAdd(physicalKey, remove);
    }

    public void Discard(DbTransaction transaction) => pending.Remove(transaction);

    public async Task<int> FlushAfterCommitAsync(DbTransaction transaction)
    {
        // Remove before awaiting: an enlisted wrapper must never drain the same work twice.
        if (!pending.Remove(transaction, out var removals)) return 0;
        using CancellationTokenSource timeout = new(RemovalTimeout);
        int failures = 0;
        foreach (var remove in removals.Values)
        {
            if (timeout.IsCancellationRequested) { failures++; continue; }
            try
            {
                // Only detached cache-adapter work may be registered here. Never abandon EF work.
                await remove(timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The database commit already succeeded; cache diagnostics must not change its result.
                failures++;
            }
        }
        return failures;
    }

    public void Dispose()
    {
        disposed = true;
        pending.Clear();
    }
}
