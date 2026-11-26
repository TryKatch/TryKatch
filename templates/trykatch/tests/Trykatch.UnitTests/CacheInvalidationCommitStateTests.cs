using System.Data;
using System.Data.Common;
using Shouldly;
using Trykatch.Infrastructure.Caching;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class CacheInvalidationCommitStateTests
{
    [TestMethod]
    public async Task EvictionsWaitForCommitAndDrainOncePerPhysicalKey()
    {
        using CacheInvalidationCommitState state = new();
        using DbTransaction transaction = new CacheCommitTransaction();
        int calls = 0;
        ValueTask Remove(CancellationToken _) { calls++; return ValueTask.CompletedTask; }
        state.Register(transaction, "first", Remove);
        state.Register(transaction, "first", Remove);
        state.Register(transaction, "second", Remove);
        calls.ShouldBe(0);

        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
        calls.ShouldBe(2);
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
        calls.ShouldBe(2);
    }

    [TestMethod]
    public async Task DiscardedTransactionCannotEvictWhileAnotherTransactionCommits()
    {
        using CacheInvalidationCommitState state = new();
        using DbTransaction rolledBack = new CacheCommitTransaction();
        using DbTransaction committed = new CacheCommitTransaction();
        int rolledBackCalls = 0;
        int committedCalls = 0;
        state.Register(rolledBack, "same-key", _ => { rolledBackCalls++; return ValueTask.CompletedTask; });
        state.Register(committed, "same-key", _ => { committedCalls++; return ValueTask.CompletedTask; });

        state.Discard(rolledBack);
        await state.FlushAfterCommitAsync(rolledBack);
        await state.FlushAfterCommitAsync(committed);

        rolledBackCalls.ShouldBe(0);
        committedCalls.ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalFailureDoesNotEscapeOrPreventRemainingEvictions()
    {
        using CacheInvalidationCommitState state = new();
        using DbTransaction transaction = new CacheCommitTransaction();
        int successfulCalls = 0;
        state.Register(transaction, "unavailable", _ => throw new InvalidOperationException("private adapter diagnostic"));
        state.Register(transaction, "available", _ => { successfulCalls++; return ValueTask.CompletedTask; });

        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(1);

        successfulCalls.ShouldBe(1);
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
    }

    [TestMethod]
    public async Task StalledCacheRemovalIsBoundedAndCannotFailTheCommittedResult()
    {
        using CacheInvalidationCommitState state = new();
        using DbTransaction transaction = new CacheCommitTransaction();
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Register(transaction, "stalled", _ => new ValueTask(stalled.Task));
        try
        {
            (await state.FlushAfterCommitAsync(transaction).WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(1);
        }
        finally { stalled.TrySetResult(); }
        (await state.FlushAfterCommitAsync(transaction)).ShouldBe(0);
    }

    [TestMethod]
    public async Task ScopeDisposalDropsUncommittedEvictions()
    {
        CacheInvalidationCommitState state = new();
        using DbTransaction transaction = new CacheCommitTransaction();
        int calls = 0;
        state.Register(transaction, "uncommitted", _ => { calls++; return ValueTask.CompletedTask; });

        state.Dispose();
        await state.FlushAfterCommitAsync(transaction);

        calls.ShouldBe(0);
        Should.Throw<ObjectDisposedException>(() => state.Register(transaction, "late", _ => ValueTask.CompletedTask));
    }

    private sealed class CacheCommitTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
