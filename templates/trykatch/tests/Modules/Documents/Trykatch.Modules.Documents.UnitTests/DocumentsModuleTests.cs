using Shouldly;
using Trykatch.Modules.Documents.Application;
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

    [TestMethod]
    public void UploadedDocumentKeepsStorageMetadataWithoutExposingTheObjectKey()
    {
        Guid organizationId = Guid.NewGuid();
        DocumentRecord document = DocumentRecord.CreateUpload(
            Guid.CreateVersion7(),
            organizationId,
            Guid.NewGuid(),
            "Board minutes",
            "Quarterly meeting",
            "minutes.pdf",
            "application/pdf",
            4_096,
            "2BB80D537B1DA3E38BD30361AA855686BDE0BAE0F7EAFDCC3B4A92B7C45D4F99",
            "organizations/hidden/document-id/minutes.pdf",
            DateTimeOffset.Parse("2026-09-14T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        DocumentDto dto = DocumentsUseCases.ToDto(document);

        dto.FileName.ShouldBe("minutes.pdf");
        dto.Metadata.MediaType.ShouldBe("application/pdf");
        dto.Metadata.SizeBytes.ShouldBe(4_096);
        dto.Metadata.Sha256.ShouldBe("2BB80D537B1DA3E38BD30361AA855686BDE0BAE0F7EAFDCC3B4A92B7C45D4F99");
        dto.GetType().GetProperty("ObjectKey").ShouldBeNull();
    }
}
