using Shouldly;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.UnitTests;

[TestClass]
public sealed class DocumentUploadOperationTests
{
    [TestMethod]
    public void LegacyUploadWithoutAnOperationKeepsNullIdentity()
    {
        DocumentRecord document = CreateUpload();

        document.UploadOperationId.ShouldBeNull();
    }

    [TestMethod]
    public void UploadOperationIdentitySurvivesMetadataAndLifecycleChanges()
    {
        Guid operationId = Guid.NewGuid();
        DocumentRecord document = CreateUpload(operationId);

        document.UploadOperationId.ShouldBe(operationId);

        document.UpdateMetadata("Renamed document", "Updated description", DateTimeOffset.UtcNow);
        document.UploadOperationId.ShouldBe(operationId);

        document.Archive(Guid.NewGuid(), DateTimeOffset.UtcNow).ShouldBeTrue();
        document.UploadOperationId.ShouldBe(operationId);

        document.RequestDeletion(Guid.NewGuid(), "Requested by the organization", DateTimeOffset.UtcNow).ShouldBeTrue();
        document.UploadOperationId.ShouldBe(operationId);

        document.Restore().ShouldBeTrue();
        document.UploadOperationId.ShouldBe(operationId);
    }

    [TestMethod]
    public void EmptyUploadOperationIdentityIsRejected()
    {
        Should.Throw<ArgumentException>(() => CreateUpload(Guid.Empty));
    }

    private static DocumentRecord CreateUpload(Guid? uploadOperationId = null)
    {
        Guid id = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string title = "Document";
        const string fileName = "document.pdf";
        const string mediaType = "application/pdf";
        const long sizeBytes = 1;
        string sha256 = new('a', 64);
        const string objectKey = "object-key";

        return uploadOperationId is null
            ? DocumentRecord.CreateUpload(id, organizationId, actorId, title, null, fileName, mediaType,
                sizeBytes, sha256, objectKey, now)
            : DocumentRecord.CreateUpload(id, organizationId, actorId, title, null, fileName, mediaType,
                sizeBytes, sha256, objectKey, now, uploadOperationId: uploadOperationId);
    }
}
