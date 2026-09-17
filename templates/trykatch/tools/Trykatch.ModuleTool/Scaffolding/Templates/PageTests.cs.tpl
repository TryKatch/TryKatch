using Shouldly;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.UnitTests;

[TestClass]
public sealed class __ENTITY__PageTests
{
    [TestMethod]
    [DataRow(0, 25)]
    [DataRow(100001, 25)]
    [DataRow(1, 0)]
    [DataRow(1, 101)]
    public void RejectsUnboundedQueries(int page, int size) =>
        Should.Throw<ArgumentException>(() => new __ENTITY__PageQuery(Page: page, PageSize: size).Validate());

    [TestMethod]
    public void RejectsUnknownLifecycleAndSortAndLongSearch()
    {
        Should.Throw<ArgumentException>(() => new __ENTITY__PageQuery(Lifecycle: "all").Validate());
        Should.Throw<ArgumentException>(() => new __ENTITY__PageQuery(Sort: "sql").Validate());
        Should.Throw<ArgumentException>(() => new __ENTITY__PageQuery(Search: new string('x', 201)).Validate());
    }

    [TestMethod]
    public void AcceptsDocumentedDefaultsAndRecoverableScope()
    {
        new __ENTITY__PageQuery().Validate();
        __ENTITY__PageQuery query = new("recoverable", 2, 100, "search", "oldest");
        query.Validate();
        query.Scope.ShouldBe(__ENTITY__QueryScope.Recoverable);
    }
}
