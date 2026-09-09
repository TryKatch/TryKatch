using TrykatchApp.Application.Auditing;
using Shouldly;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class AuditEventDefinitionsTests
{
    [TestMethod]
    public void KnownActionsExposeReadableStableMetadata()
    {
        AuditEventDefinition definition = AuditEventDefinitions.Resolve(AuditActions.MembershipUpdated);

        definition.Title.ShouldBe("Member access updated");
        definition.Category.ShouldBe("Access");
        definition.DescriptionTemplate.ShouldContain("{actor}");
        definition.DescriptionTemplate.ShouldContain("{target}");
    }

    [TestMethod]
    public void UnknownActionsFailReadableWithoutBreakingTheFeed()
    {
        AuditEventDefinition definition = AuditEventDefinitions.Resolve("billing.exported");

        definition.Title.ShouldBe("Billing exported");
        definition.Category.ShouldBe("Billing");
        definition.Action.ShouldBe("billing.exported");
    }
}
