using Shouldly;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.Modules.Documents.UnitTests;

[TestClass]
public sealed class DocumentsModuleTests
{
    [TestMethod]
    public void DescriptorResolvesTheModuleOwnedEntity()
    {
        DocumentsModule module = new();
        module.Descriptor.Id.ShouldBe("documents");
        module.Descriptor.Requires.ShouldContain("projects");
        module.Descriptor.DataResources.Single().EntityType.ShouldBe(typeof(DocumentRecord).FullName);
    }
}
