using Shouldly;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.UnitTests;

[TestClass]
public sealed class __ENTITY__WorkflowTests
{
    private static __ENTITY__Record CreateRecord() => __ENTITY__Record.Create(
        Guid.NewGuid(), Guid.NewGuid(), __FIXTURE_ARGUMENTS__, DateTimeOffset.UnixEpoch);

    [TestMethod]
    public void CreationUsesDeclaredInitialStateAndNonemptyVersion()
    {
        __ENTITY__Record record = CreateRecord();
        record.WorkflowState.ShouldBe(__ENTITY__WorkflowState.__INITIAL_STATE__);
        record.Version.ShouldNotBe(Guid.Empty);
    }

    [TestMethod]
    public void RecoveryPreservesBusinessStateAndChangesVersion()
    {
        __ENTITY__Record record = CreateRecord();
        __ENTITY__WorkflowState state = record.WorkflowState;
        Guid initialVersion = record.Version;
        record.Archive(Guid.NewGuid(), DateTimeOffset.UtcNow);
        record.Version.ShouldNotBe(initialVersion);
        Guid archivedVersion = record.Version;
        record.Restore();
        record.Version.ShouldNotBe(archivedVersion);
        record.WorkflowState.ShouldBe(state);
    }

    __ARCHIVED_ACTION_TESTS__
}
