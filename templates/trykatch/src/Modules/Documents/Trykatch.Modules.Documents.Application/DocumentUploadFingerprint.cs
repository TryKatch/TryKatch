using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Application;

/// <summary>Creates a stable identity for the metadata persisted with a document upload.</summary>
public static class DocumentUploadFingerprint
{
    private const string FormatVersion = "document-upload-v1";

    public static string Compute(UploadDocumentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? error = DocumentUploadPolicy.Validate(command);
        if (error is not null) throw new ArgumentException(error, nameof(command));

        CanonicalUpload canonical = new(
            FormatVersion,
            command.Title.Trim(),
            command.Description?.Trim() ?? string.Empty,
            DocumentUploadPolicy.SafeFileName(command.FileName),
            command.MediaType,
            command.SizeBytes.ToString(CultureInfo.InvariantCulture),
            command.Sha256.ToLowerInvariant(),
            command.DocumentType ?? DocumentTypes.Other);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record CanonicalUpload(
        string Version,
        string Title,
        string Description,
        string FileName,
        string MediaType,
        string SizeBytes,
        string Sha256,
        string DocumentType);
}
