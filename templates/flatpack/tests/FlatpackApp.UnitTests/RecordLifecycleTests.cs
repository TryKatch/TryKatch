using FlatpackApp.Application.Common;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class RecordLifecycleTests
{
    [TestMethod]
    public void RecoverableFilterIsASupportedExplicitScope()
    {
        bool parsed = RecordLifecycle.TryParseFilter("recoverable", out RecordLifecycleFilter filter);

        Assert.IsTrue(parsed);
        Assert.AreEqual(RecordLifecycleFilter.Recoverable, filter);
    }

    [TestMethod]
    public void UnknownFilterIsRejected()
    {
        Assert.IsFalse(RecordLifecycle.TryParseFilter("trash-everything", out _));
    }
}
