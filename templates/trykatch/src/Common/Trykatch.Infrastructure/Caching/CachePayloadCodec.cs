using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Caching;

internal static class CachePayloadCodec
{
    private const byte FormatVersion = 1;
    private const int TypeHashLength = 32;
    private const int HeaderLength = sizeof(byte) + TypeHashLength;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeof(IOrganizationOwned).IsAssignableFrom(typeInfo.Type))
                throw new NotSupportedException("Organization cache payloads must contain detached read DTOs.");
        });
        return new() { MaxDepth = 16, TypeInfoResolver = resolver };
    }

    public static byte[] Encode<T>(T value)
    {
        byte[] typeHash = SHA256.HashData(Encoding.UTF8.GetBytes(GetAssemblyQualifiedTypeName<T>()));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        byte[] payload = new byte[HeaderLength + json.Length];
        payload[0] = FormatVersion;
        typeHash.CopyTo(payload, 1);
        json.CopyTo(payload, HeaderLength);
        return payload;
    }

    public static bool TryDecode<T>(ReadOnlySpan<byte> payload, out T? value)
    {
        value = default;
        if (payload.Length < HeaderLength || payload[0] != FormatVersion)
            return false;

        byte[] expectedTypeHash = SHA256.HashData(Encoding.UTF8.GetBytes(GetAssemblyQualifiedTypeName<T>()));
        if (!CryptographicOperations.FixedTimeEquals(payload.Slice(1, TypeHashLength), expectedTypeHash))
            return false;

        try
        {
            value = JsonSerializer.Deserialize<T>(payload[HeaderLength..], SerializerOptions);
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    private static string GetAssemblyQualifiedTypeName<T>() =>
        typeof(T).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cache payload type has no assembly-qualified name.");
}
