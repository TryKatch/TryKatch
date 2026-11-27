using System.Globalization;
using System.Text;
using System.Text.Json;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Persistence;

internal static class OutboxPayloadSize
{
    // jsonb::text adds at most one ASCII space per comma or colon. Bound the SQL
    // read independently, then enforce the compact event limit before dispatch.
    internal const int MaximumStoredBytes = 2 * ModuleWorkRegistrationRules.HostMaximumPayloadBytes;

    internal static bool FitsStoredJson(string payload, int maximumBytes)
    {
        if (payload.Length > MaximumStoredBytes) return false;
        int bytes = Encoding.UTF8.GetByteCount(payload);
        if (bytes <= maximumBytes) return true;
        if (bytes > MaximumStoredBytes) return false;

        bool inString = false;
        for (int i = 0; i < payload.Length; i++)
        {
            char current = payload[i];
            if (inString && current == '\\') i++;
            else if (current == '"') inString = !inString;
            else if (!inString && current is ' ' or '\t' or '\r' or '\n') bytes--;
        }
        return bytes <= maximumBytes;
    }

    internal static bool FitsSerializedJson(ReadOnlySpan<byte> utf8, int maximumBytes)
    {
        // Preserve the producer's existing wire limit. PostgreSQL can also expand
        // exponent-form numbers even after insignificant whitespace is removed.
        if (utf8.Length > maximumBytes) return false;
        if (utf8.IndexOfAny((byte)'e', (byte)'E') < 0) return true;

        long storedBytes = utf8.Length;
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            ReadOnlySpan<byte> token = reader.ValueSpan;
            if (reader.TokenType == JsonTokenType.Number)
            {
                int exponentIndex = token.IndexOfAny((byte)'e', (byte)'E');
                if (exponentIndex >= 0)
                {
                    long? length = DecimalLength(token, exponentIndex);
                    if (length is null) return false;
                    storedBytes += length.Value - token.Length;
                }
            }
            else if (reader.ValueIsEscaped && reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                storedBytes += JsonbStringContentLength(reader.GetString()!) - token.Length;
            }
        }
        return storedBytes <= maximumBytes;
    }

    // PostgreSQL numeric output uses plain decimal notation, preserves fractional
    // scale, and suppresses leading zeroes and the sign on zero. Count without
    // allocating the potentially enormous expanded decimal.
    private static long? DecimalLength(ReadOnlySpan<byte> token, int exponentIndex)
    {
        if (!int.TryParse(token[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out int exponent))
            return null;

        bool negative = token[0] == '-';
        ReadOnlySpan<byte> digits = token[(negative ? 1 : 0)..exponentIndex];
        int point = digits.IndexOf((byte)'.');
        int integerDigits = point < 0 ? digits.Length : point;
        int fractionDigits = point < 0 ? 0 : digits.Length - point - 1;
        int leadingZeroes = 0;
        foreach (byte digit in digits)
        {
            if (digit == '.') continue;
            if (digit != '0') break;
            leadingZeroes++;
        }

        bool zero = leadingZeroes == integerDigits + fractionDigits;
        long integerLength = zero ? 1 : Math.Max(1, (long)integerDigits + exponent - leadingZeroes);
        long scale = Math.Max(0, (long)fractionDigits - exponent);
        return integerLength + (negative && !zero ? 1 : 0) + (scale > 0 ? 1 + scale : 0);
    }

    // jsonb emits UTF-8 characters directly and escapes JSON quotes, backslashes
    // and control characters. The caller already counted the surrounding quotes.
    private static long JsonbStringContentLength(string value)
    {
        long length = Encoding.UTF8.GetByteCount(value);
        foreach (char c in value)
        {
            length += c switch
            {
                '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 1,
                < ' ' => 5,
                _ => 0
            };
        }
        return length;
    }
}
