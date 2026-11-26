using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Trykatch.Infrastructure.Caching;

internal sealed partial class CacheInvalidationTransactionInterceptor(
    CacheInvalidationCommitState state,
    ILogger<CacheInvalidationTransactionInterceptor> logger) : DbTransactionInterceptor
{
    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        // Npgsql may reuse its transaction object after disposal/implicit rollback.
        state.Discard(result);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        state.Discard(result);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        FlushAsync(transaction).GetAwaiter().GetResult();

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        FlushAsync(transaction);

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        state.Discard(transaction);

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        state.Discard(transaction);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        state.Discard(transaction);

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        state.Discard(transaction);
        return Task.CompletedTask;
    }

    private async Task FlushAsync(DbTransaction transaction)
    {
        // Ignore request cancellation after confirmed commit. The state owns a bounded cache-only deadline.
        int failures = await state.FlushAfterCommitAsync(transaction).ConfigureAwait(false);
        if (failures > 0)
            RemovalUnavailable(logger, failures);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post-commit cache removal was unavailable for {RemovalCount} entries; durable generations remain authoritative.")]
    private static partial void RemovalUnavailable(ILogger logger, int removalCount);
}
