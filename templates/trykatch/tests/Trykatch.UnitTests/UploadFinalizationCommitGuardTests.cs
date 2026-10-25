using System.Data;
using System.Data.Common;
using Shouldly;
using Trykatch.Infrastructure.Quotas;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class UploadFinalizationCommitGuardTests
{
    [TestMethod]
    public void AttemptFromAnotherGuardCannotCompletePendingFinalization()
    {
        using TransactionIdentity transaction = new();
        UploadFinalizationCommitGuard first = new();
        UploadFinalizationCommitGuard second = new();
        var firstAttempt = first.Begin(transaction);
        _ = second.Begin(transaction);

        Should.Throw<UploadFinalizationIncompleteException>(() => second.Complete(firstAttempt));
        Should.Throw<UploadFinalizationIncompleteException>(() => second.EnsureCanCommit(transaction));
        first.Complete(firstAttempt);
        Should.NotThrow(() => first.EnsureCanCommit(transaction));
    }

    [TestMethod]
    public void RejectedCommitCannotBeRepairedByCompletingItsPendingAttempt()
    {
        using TransactionIdentity transaction = new();
        UploadFinalizationCommitGuard guard = new();
        var attempt = guard.Begin(transaction);

        Should.Throw<UploadFinalizationIncompleteException>(() => guard.EnsureCanCommit(transaction));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(attempt));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Begin(transaction));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.EnsureCanCommit(transaction));
    }

    [TestMethod]
    public void StartingNewTransactionLifetimeRejectsTheOldAttemptEvenWhenProviderReusesItsObject()
    {
        using TransactionIdentity transaction = new();
        UploadFinalizationCommitGuard guard = new();
        var old = guard.Begin(transaction);
        guard.Reset(transaction);
        var current = guard.Begin(transaction);

        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(old));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(current));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.EnsureCanCommit(transaction));
    }

    [TestMethod]
    public void CompletingAnAttemptTwiceCannotClearTheNextPendingAttempt()
    {
        using TransactionIdentity transaction = new();
        UploadFinalizationCommitGuard guard = new();
        var first = guard.Begin(transaction);
        guard.Complete(first);
        var second = guard.Begin(transaction);

        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(first));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(second));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.EnsureCanCommit(transaction));
    }

    // Identity only: SQL and EF commit interception are exercised by the PostgreSQL integration cases.
    private sealed class TransactionIdentity : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
