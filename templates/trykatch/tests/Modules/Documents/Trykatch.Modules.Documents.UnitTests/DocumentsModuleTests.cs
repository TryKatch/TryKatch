using Shouldly;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.Modules.Documents.UnitTests;

[TestClass]
public sealed class DocumentsModuleTests
{
    [TestMethod]
    public void DocumentTypeIsBusinessMetadataAndLegacyEditsPreserveIt()
    {
        DocumentRecord document = DocumentRecord.CreateUpload(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Invoice", null, "invoice.pdf", "application/pdf",
            100, new string('A', 64), "opaque-key", DateTimeOffset.UtcNow, "invoice");

        DocumentsUseCases.ToDto(document).DocumentType.ShouldBe("invoice");
        document.UpdateMetadata("Renamed invoice", null, DateTimeOffset.UtcNow);
        document.DocumentType.ShouldBe("invoice");
        document.UpdateMetadata("Report", null, DateTimeOffset.UtcNow, "report");
        document.DocumentType.ShouldBe("report");
        document.MediaType.ShouldBe("application/pdf");
        document.Archive(Guid.NewGuid(), DateTimeOffset.UtcNow);
        document.Restore();
        document.DocumentType.ShouldBe("report");
    }

    [TestMethod]
    public void InvalidDocumentTypeCannotPartiallyMutateAnEntity()
    {
        DocumentRecord document = DocumentRecord.CreateUpload(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Original", null, "file.pdf", "application/pdf",
            100, new string('A', 64), "opaque-key", DateTimeOffset.UtcNow);

        document.DocumentType.ShouldBe("other");
        Should.Throw<ArgumentException>(() => document.UpdateMetadata("Changed", null, DateTimeOffset.UtcNow, "application/pdf"));
        document.Title.ShouldBe("Original");
        document.DocumentType.ShouldBe("other");
    }

    [TestMethod]
    [DataRow("invoice")]
    [DataRow("contract")]
    [DataRow("certificate")]
    [DataRow("report")]
    [DataRow("other")]
    public void UploadPolicyAcceptsTheSupportedBusinessTypes(string documentType)
    {
        using MemoryStream content = new([1]);
        DocumentUploadPolicy.Validate(new("Document", null, "file.pdf", "application/pdf", 1,
            new string('A', 64), content, documentType)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Invoice")]
    [DataRow("application/pdf")]
    [DataRow("unknown")]
    public void UploadPolicyRejectsUnknownOrNoncanonicalBusinessTypes(string documentType)
    {
        using MemoryStream content = new([1]);
        DocumentUploadPolicy.Validate(new("Document", null, "file.pdf", "application/pdf", 1,
            new string('A', 64), content, documentType)).ShouldBe("Choose a supported document type.");
        DocumentUploadPolicy.ValidateMetadata("Document", null, documentType)
            .ShouldBe("Choose a supported document type.");
    }

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
