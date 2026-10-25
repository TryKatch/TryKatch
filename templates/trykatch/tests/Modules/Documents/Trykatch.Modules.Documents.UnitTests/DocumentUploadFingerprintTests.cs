using Shouldly;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.UnitTests;

[TestClass]
public sealed class DocumentUploadFingerprintTests
{
    [TestMethod]
    public void VersionOneFingerprintRemainsCompatibleWithPersistedOperationIdentity()
    {
        using MemoryStream content = new([1, 2, 3]);
        // Protocol fixture: changing canonical encoding requires a new format version and retry compatibility.
        DocumentUploadFingerprint.Compute(CreateCommand(content))
            .ShouldBe("541fb9af0cbb69ea1d13a663db40ce18031dc60f94df55866ea79f5945b87210");
    }

    [TestMethod]
    public void EquivalentNormalizedMetadataHasTheSameFingerprint()
    {
        using MemoryStream firstContent = new([1, 2, 3]);
        using MemoryStream secondContent = new([4, 5, 6]);
        UploadDocumentCommand first = new("  Quarterly report  ", "  Reviewed by finance  ",
            "reports\\2026/report.pdf  ", "application/pdf", 3, new string('A', 64), firstContent);
        UploadDocumentCommand second = new("Quarterly report", "Reviewed by finance", "report.pdf",
            "application/pdf", 3, new string('a', 64), secondContent, DocumentTypes.Other);

        DocumentUploadFingerprint.Compute(first).ShouldBe(DocumentUploadFingerprint.Compute(second));
    }

    [TestMethod]
    public void NullDescriptionMatchesEmptyDescription()
    {
        using MemoryStream firstContent = new([1]);
        using MemoryStream secondContent = new([2]);
        UploadDocumentCommand first = new("Report", null, "report.pdf", "application/pdf", 1,
            new string('b', 64), firstContent);
        UploadDocumentCommand second = new("Report", "", "report.pdf", "application/pdf", 1,
            new string('B', 64), secondContent, DocumentTypes.Other);

        DocumentUploadFingerprint.Compute(first).ShouldBe(DocumentUploadFingerprint.Compute(second));
    }

    [TestMethod]
    public void SameLengthWithDifferentPayloadDigestHasDifferentFingerprint()
    {
        using MemoryStream firstContent = new([1, 2, 3]);
        using MemoryStream secondContent = new([1, 2, 3]);
        UploadDocumentCommand first = CreateCommand(firstContent, sha256: new string('1', 64));
        UploadDocumentCommand second = CreateCommand(secondContent, sha256: new string('2', 64));

        DocumentUploadFingerprint.Compute(first).ShouldNotBe(DocumentUploadFingerprint.Compute(second));
    }

    [TestMethod]
    public void EveryPersistedUploadMetadataValueChangesTheFingerprint()
    {
        using MemoryStream content = new([7, 8, 9]);
        UploadDocumentCommand original = CreateCommand(content);
        string fingerprint = DocumentUploadFingerprint.Compute(original);
        UploadDocumentCommand[] changed =
        [
            original with { Title = "Different report" },
            original with { Description = "Different description" },
            original with { DocumentType = "invoice" },
            original with { FileName = "different.pdf" },
            original with { MediaType = "image/png" },
            original with { SizeBytes = original.SizeBytes + 1 }
        ];

        foreach (UploadDocumentCommand variant in changed)
            DocumentUploadFingerprint.Compute(variant).ShouldNotBe(fingerprint);
    }

    [TestMethod]
    public void DelimiterLikeMetadataCannotCollideThroughAmbiguousConcatenation()
    {
        using MemoryStream firstContent = new([1]);
        using MemoryStream secondContent = new([1]);
        UploadDocumentCommand first = CreateCommand(firstContent) with { Title = "ab", Description = "c" };
        UploadDocumentCommand second = CreateCommand(secondContent) with { Title = "a", Description = "bc" };

        DocumentUploadFingerprint.Compute(first).ShouldNotBe(DocumentUploadFingerprint.Compute(second));
    }

    [TestMethod]
    public void ComputationIsDeterministicAndDoesNotConsumeContent()
    {
        using MemoryStream content = new([1, 2, 3]);
        content.Position = 1;
        UploadDocumentCommand command = CreateCommand(content);

        string first = DocumentUploadFingerprint.Compute(command);
        long positionAfterFirst = content.Position;
        string second = DocumentUploadFingerprint.Compute(command);

        second.ShouldBe(first);
        content.Position.ShouldBe(1);
        positionAfterFirst.ShouldBe(1);
    }

    [TestMethod]
    public void InvalidUploadIsRejected()
    {
        using MemoryStream content = new([1]);
        UploadDocumentCommand invalid = CreateCommand(content) with { Sha256 = "not-a-sha256" };

        Should.Throw<ArgumentException>(() => DocumentUploadFingerprint.Compute(invalid));
    }

    private static UploadDocumentCommand CreateCommand(Stream content, string? sha256 = null) =>
        new("Operations report", "Reviewed", "report.pdf", "application/pdf", 3,
            sha256 ?? new string('c', 64), content, DocumentTypes.Other);
}
