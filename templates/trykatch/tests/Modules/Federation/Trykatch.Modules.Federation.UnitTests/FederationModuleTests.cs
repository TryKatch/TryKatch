using Shouldly;
using Trykatch.Modules.Federation.Infrastructure;

namespace Trykatch.Modules.Federation.UnitTests;

[TestClass]
public sealed class FederationModuleTests
{
    [TestMethod]
    public void DescriptorKeepsFederationAsAPlatformModule()
    {
        FederationModule module = new();
        module.Descriptor.Id.ShouldBe("federation");
        module.Descriptor.DataResources.ShouldHaveSingleItem().Ownership.ShouldBe(Trykatch.Modules.ModuleDataOwnership.Platform);
    }
}
