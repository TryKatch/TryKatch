using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QuotaInventoryPageTests
{
    [TestMethod]
    public void ValidBoundedPagesKeepOpaqueCursorAndInt64Units()
    {
        ModuleQuotaInventoryEntry entry = new(Guid.NewGuid(), Guid.NewGuid(), long.MaxValue);
        QuotaInventoryPageValidator.Validate(new([], null, true), null);
        QuotaInventoryPageValidator.Validate(new([entry], null, true), "previous");
        QuotaInventoryPageValidator.Validate(new([entry], "next-opaque", false), "previous");
        QuotaInventoryPageValidator.Validate(new(Enumerable.Range(0, 500)
            .Select(_ => new ModuleQuotaInventoryEntry(Guid.NewGuid(), Guid.NewGuid(), 1)).ToArray(), null, true), null);
    }

    [TestMethod]
    public void MalformedOrUnboundedPagesCannotAdvanceCheckpoint()
    {
        ModuleQuotaInventoryEntry valid = new(Guid.NewGuid(), Guid.NewGuid(), 1);
        ModuleQuotaInventoryPage[] invalid =
        [
            null!, new(null!, null, true),
            new([null!], null, true),
            new([valid with { SourceId = Guid.Empty }], null, true),
            new([valid with { OperationId = Guid.Empty }], null, true),
            new([valid with { Units = 0 }], null, true),
            new([valid with { Units = -1 }], null, true),
            new(Enumerable.Repeat(valid, 501).ToArray(), null, true),
            new([valid], "unexpected", true),
            new([valid], null, false), new([valid], "", false),
            new([valid], "previous", false),
            new([valid], new string('x', 1025), false),
            new([], "next", false)
        ];
        foreach (ModuleQuotaInventoryPage page in invalid)
            Should.Throw<OrganizationQuotaUnavailableException>(() => QuotaInventoryPageValidator.Validate(page, "previous"));
    }
}
