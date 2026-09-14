using Shouldly;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleTransactionCompensationTests
{
    [TestMethod]
    public async Task RollbackRunsEveryCompensationInReverseOrderOnlyOnce()
    {
        ModuleTransactionCompensation transaction = new();
        List<int> calls = [];
        transaction.EnlistRollback(_ =>
        {
            calls.Add(1);
            return Task.CompletedTask;
        });
        transaction.EnlistRollback(_ =>
        {
            calls.Add(2);
            return Task.CompletedTask;
        });

        await transaction.RollbackAsync();
        await transaction.RollbackAsync();

        calls.ShouldBe([2, 1]);
    }

    [TestMethod]
    public async Task SuccessfulCompletionDiscardsCompensations()
    {
        ModuleTransactionCompensation transaction = new();
        bool called = false;
        transaction.EnlistRollback(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        transaction.Complete();
        await transaction.RollbackAsync();

        called.ShouldBeFalse();
    }

    [TestMethod]
    public async Task IndeterminateCommitRetainsExternalResourceForReconciliation()
    {
        ModuleTransactionCompensation transaction = new();
        bool deleted = false;
        transaction.EnlistRollback(_ =>
        {
            deleted = true;
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<IOException>(() =>
            transaction.CompensateAfterConfirmedRollbackAsync(
                _ => throw new IOException("commit acknowledgement lost")));

        deleted.ShouldBeFalse();
        await transaction.RollbackAsync();
        deleted.ShouldBeTrue("the retained callback remains available to a reconciliation path");
    }
}
