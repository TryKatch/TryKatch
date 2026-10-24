using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Trykatch.Modules;

namespace Trykatch.Modules.AspNetCore;

public sealed record ModulePreparedMutationRequest(string KeyHash, string RequestHash);

public sealed class ModuleMutationRequestException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>Prepares a bounded request identity before endpoint binding.</summary>
public static class ModuleRequestFingerprint
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ModulePreparedMutationRequest?> PrepareAsync(
        HttpRequest request,
        ModuleMutationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string? key = ReadKey(request, policy.KeyRequired);
        if (key is null)
            return null;

        string? mediaType = ReadJsonMediaType(request.ContentType);
        ValidateContentEncoding(request);
        if (request.ContentLength is > 0 && request.ContentLength > policy.MaximumRequestBytes)
            throw Invalid(StatusCodes.Status413PayloadTooLarge, "request_too_large");

        byte[] body;
        if (request.ContentLength == 0 || ReferenceEquals(request.Body, Stream.Null)
            || (request.Body.CanSeek && request.Body.Position >= request.Body.Length))
        {
            body = [];
        }
        else
        {
            body = await ReadBoundedBodyAsync(request, policy.MaximumRequestBytes, cancellationToken);
        }

        bool hasBody = body.Length > 0;
        if (hasBody && mediaType is null)
            throw Invalid(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        string? canonicalBodyHash = null;
        if (hasBody)
        {
            string json;
            try
            {
                json = StrictUtf8.GetString(body);
                using JsonDocument document = JsonDocument.Parse(json);
                RejectCaseInsensitivePropertyAliases(document.RootElement);
                canonicalBodyHash = ModuleCanonicalJson.ComputeHash(json);
            }
            catch (DecoderFallbackException)
            {
                throw Invalid(StatusCodes.Status400BadRequest, "invalid_json");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw Invalid(StatusCodes.Status400BadRequest, "invalid_json");
            }
        }

        string keyHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)));
        string requestHash = ComputeRequestHash(request, mediaType, canonicalBodyHash);
        request.Body = new MemoryStream(body, writable: false);
        return new ModulePreparedMutationRequest(keyHash, requestHash);
    }

    private static void RejectCaseInsensitivePropertyAliases(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                // Web JSON binding can treat differently cased names as the same property.
                // Sorting those aliases would otherwise hide order-dependent bound values.
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("Ambiguous JSON property names cannot identify a mutation safely.");
                RejectCaseInsensitivePropertyAliases(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectCaseInsensitivePropertyAliases(item);
        }
    }

    private static string? ReadKey(HttpRequest request, bool required)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out StringValues values))
        {
            if (required)
                throw Invalid(StatusCodes.Status400BadRequest, "idempotency_key_required");
            return null;
        }

        if (values.Count != 1 || !IsValidKey(values[0]))
            throw Invalid(StatusCodes.Status400BadRequest, "idempotency_key_invalid");
        return values[0]!;
    }

    private static bool IsValidKey(string? key)
    {
        if (key is not { Length: > 0 and <= 128 } || !IsAsciiAlphaNumeric(key[0]))
            return false;
        foreach (char character in key)
        {
            if (!IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or ':' or '-'))
                return false;
        }
        return true;
    }

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string? ReadJsonMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed))
            throw Invalid(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        string mediaType = parsed.MediaType?.ToLowerInvariant() ?? string.Empty;
        bool supported = mediaType == "application/json"
            || mediaType.StartsWith("application/", StringComparison.Ordinal)
            && mediaType.EndsWith("+json", StringComparison.Ordinal);
        string? charset = parsed.CharSet?.Trim().Trim('"');
        if (!supported || charset is not null && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase))
            throw Invalid(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
        return mediaType;
    }

    private static void ValidateContentEncoding(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Content-Encoding", out StringValues encodings))
            return;
        foreach (string? encoding in encodings)
        {
            if (!string.IsNullOrWhiteSpace(encoding)
                && !string.Equals(encoding.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
                throw Invalid(StatusCodes.Status415UnsupportedMediaType, "unsupported_content_encoding");
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new(Math.Min(maximumBytes, 8_192));
        byte[] chunk = new byte[Math.Min(8_192, checked(maximumBytes + 1))];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = maximumBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0)
                throw Invalid(StatusCodes.Status413PayloadTooLarge, "request_too_large");
            int read = await request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes)
                throw Invalid(StatusCodes.Status413PayloadTooLarge, "request_too_large");
        }
        return buffer.ToArray();
    }

    private static string ComputeRequestHash(HttpRequest request, string? mediaType, string? canonicalBodyHash)
    {
        using MemoryStream envelope = new();
        using (Utf8JsonWriter writer = new(envelope))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol", 1);
            writer.WriteString("method", request.Method.ToUpperInvariant());
            writer.WriteString("path", request.PathBase.ToUriComponent() + request.Path.ToUriComponent());
            writer.WriteString("query", request.QueryString.Value ?? string.Empty);
            if (mediaType is null) writer.WriteNull("mediaType");
            else writer.WriteString("mediaType", mediaType);
            if (canonicalBodyHash is null) writer.WriteBoolean("bodyAbsent", true);
            else writer.WriteString("bodyHash", canonicalBodyHash);
            writer.WriteEndObject();
        }
        return ModuleCanonicalJson.ComputeHash(Encoding.UTF8.GetString(envelope.ToArray()));
    }

    private static ModuleMutationRequestException Invalid(int statusCode, string code) => new(statusCode, code);
}
