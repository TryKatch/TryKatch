using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class UploadFinalizationTransactionInterceptor(UploadFinalizationCommitGuard guard) : DbTransactionInterceptor
{
    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        guard.Reset(result);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        guard.Reset(result);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        guard.EnsureCanCommit(transaction);
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureCanCommit(transaction);
        return ValueTask.FromResult(result);
    }
}
