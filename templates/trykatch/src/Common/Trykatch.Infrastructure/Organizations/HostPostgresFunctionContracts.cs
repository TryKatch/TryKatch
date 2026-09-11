using System.Text;

namespace Trykatch.Infrastructure.Organizations;

internal static class HostPostgresFunctionContracts
{
    public const string RedactLegacyOutboxError = "platform.redact_legacy_outbox_error()";

    public const string RedactLegacyOutboxErrorBody = """
        BEGIN
          IF NEW."LastError" IS NOT NULL THEN
            NEW."LastErrorCode" := COALESCE(NEW."LastErrorCode", 'legacy_unclassified');
            NEW."LastErrorType" := COALESCE(NEW."LastErrorType", 'legacy_exception');
            NEW."LastError" := NULL;
          END IF;
          RETURN NEW;
        END
        """;

    public const string RedactLegacyOutboxErrorTrigger = """
        CREATE TRIGGER redact_legacy_outbox_error BEFORE INSERT OR UPDATE OF "LastError"
        ON platform.outbox_messages FOR EACH ROW EXECUTE FUNCTION platform.redact_legacy_outbox_error()
        """;

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RedactLegacyOutboxError
    };

    public static string Normalize(string statement)
    {
        StringBuilder result = new();
        char quote = '\0';
        foreach (char character in statement)
        {
            if (quote != '\0')
            {
                result.Append(character);
                if (character == quote) quote = '\0';
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                result.Append(character);
            }
            else if (!char.IsWhiteSpace(character) && character != ';')
            {
                result.Append(char.ToLowerInvariant(character));
            }
        }
        return result.ToString();
    }
}
