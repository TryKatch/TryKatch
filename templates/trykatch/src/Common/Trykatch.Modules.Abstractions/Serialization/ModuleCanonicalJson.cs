using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Trykatch.Modules;

/// <summary>Shared canonical JSON hashing contract.</summary>
public static class ModuleCanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false
    };

    public static string ComputeHash(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream canonical = new();
        using (Utf8JsonWriter writer = new(canonical, WriterOptions))
            WriteCanonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previousName = null;
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (string.Equals(previousName, property.Name, StringComparison.Ordinal))
                        throw new InvalidOperationException("Duplicate JSON object properties cannot be hashed safely.");
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                    previousName = property.Name;
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()));
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON token.");
        }
    }

    private static string NormalizeNumber(string token)
    {
        int exponentIndex = token.IndexOfAny(['e', 'E']);
        string significand = exponentIndex < 0 ? token : token[..exponentIndex];
        BigInteger exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(token[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        bool negative = significand[0] == '-';
        if (negative) significand = significand[1..];

        int decimalIndex = significand.IndexOf('.');
        int fractionalDigits = decimalIndex < 0 ? 0 : significand.Length - decimalIndex - 1;
        string digits = decimalIndex < 0 ? significand : significand.Remove(decimalIndex, 1);
        exponent -= fractionalDigits;
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return "0";

        int end = digits.Length;
        while (end > 1 && digits[end - 1] == '0')
        {
            end--;
            exponent++;
        }
        digits = digits[..end];
        return string.Concat(negative ? "-" : string.Empty, digits, "e", exponent.ToString(CultureInfo.InvariantCulture));
    }
}
