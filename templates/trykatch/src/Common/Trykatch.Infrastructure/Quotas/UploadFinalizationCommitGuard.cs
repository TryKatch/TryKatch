using System.Data.Common;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class UploadFinalizationCommitGuard
{
    private readonly object gate = new();
    private readonly Dictionary<DbTransaction, TransactionState> transactions = new(ReferenceEqualityComparer.Instance);

    internal Attempt Begin(DbTransaction transaction)
    {
        lock (gate)
        {
            TransactionState state = GetState(transaction);
            if (state.Failed || state.Pending is not null)
            {
                state.Failed = true;
                throw new UploadFinalizationIncompleteException();
            }
            Attempt attempt = new(this, transaction);
            state.Pending = attempt;
            return attempt;
        }
    }

    internal void Complete(Attempt attempt)
    {
        lock (gate)
        {
            TransactionState state = GetState(attempt.Transaction);
            if (state.Failed || !ReferenceEquals(attempt.Issuer, this) || !ReferenceEquals(state.Pending, attempt))
            {
                state.Failed = true;
                throw new UploadFinalizationIncompleteException();
            }
            state.Pending = null;
        }
    }

    internal void EnsureCanCommit(DbTransaction transaction)
    {
        lock (gate)
        {
            if (transactions.TryGetValue(transaction, out TransactionState? state)
                && (state.Failed || state.Pending is not null))
            {
                // A rejected commit must not be made valid by a late callback completion.
                state.Failed = true;
                throw new UploadFinalizationIncompleteException();
            }
        }
    }

    internal void Reset(DbTransaction transaction)
    {
        // Only TransactionStarted calls this: Npgsql can reuse an object for a new lifetime.
        lock (gate) transactions.Remove(transaction);
    }

    private TransactionState GetState(DbTransaction transaction)
    {
        if (!transactions.TryGetValue(transaction, out TransactionState? state))
            transactions.Add(transaction, state = new());
        return state;
    }

    private sealed class TransactionState
    {
        internal Attempt? Pending { get; set; }
        internal bool Failed { get; set; }
    }

    internal sealed class Attempt(UploadFinalizationCommitGuard issuer, DbTransaction transaction)
    {
        internal UploadFinalizationCommitGuard Issuer { get; } = issuer;
        internal DbTransaction Transaction { get; } = transaction;
    }
}

internal sealed class UploadFinalizationIncompleteException() : InvalidOperationException(
    "An upload finalization did not complete; the owning transaction must roll back.");
