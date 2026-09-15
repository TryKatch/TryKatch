namespace Trykatch.Modules.Documents.Domain;

/// <summary>Stable business classifications, independent of a file's media type.</summary>
public static class DocumentTypes
{
    public const string Other = "other";

    public static bool IsValid(string value) => value is
        "invoice" or "contract" or "certificate" or "report" or Other;

    public static string RequireValid(string value) => IsValid(value)
        ? value
        : throw new ArgumentException("Choose a supported document type.", nameof(value));
}
