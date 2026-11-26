using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Caching;

internal static class CacheKeyPolicy
{
    private const int EncodingVersion = 1;
    private const int MaximumIdentifierLength = 120;
    private const int MaximumTtlPolicyKeyLength = 64;
    private const int MaximumPhysicalKeyLength = 1024;
    private const string InvalidValueMessage = "The organization cache key or host context is invalid.";

    public static string LogicalHash(
        ModuleCacheKey key,
        string applicationNamespace,
        string environment,
        Guid organizationId,
        Guid actorId)
    {
        Validate(key, applicationNamespace, environment, organizationId, actorId);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, EncodingVersion);
        AppendString(hash, applicationNamespace);
        AppendString(hash, environment);
        AppendString(hash, organizationId.ToString("N"));
        AppendInt32(hash, key.Audience == ModuleCacheAudience.CurrentActor ? 1 : 0);
        if (key.Audience == ModuleCacheAudience.CurrentActor)
            AppendString(hash, actorId.ToString("N"));
        AppendString(hash, key.ModuleId);
        AppendString(hash, key.ReadModelKey);
        AppendInt32(hash, key.SchemaVersion);
        AppendInt32(hash, key.PolicyVersion);
        AppendString(hash, key.ParametersHash);
        AppendInt32(hash, (int)key.Audience);
        AppendString(hash, key.TtlPolicyKey);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string PhysicalKey(string logicalHash, Guid generation)
    {
        if (!IsLowerHex(logicalHash, 64))
            throw InvalidValue();

        string physicalKey = $"org-cache:v1:{logicalHash}:{generation:N}";
        if (physicalKey.Length > MaximumPhysicalKeyLength)
            throw InvalidValue();
        return physicalKey;
    }

    private static void Validate(
        ModuleCacheKey? key,
        string? applicationNamespace,
        string? environment,
        Guid organizationId,
        Guid actorId)
    {
        if (key is null
            || !IsLowerKebab(key.ModuleId, MaximumIdentifierLength)
            || !IsReadModelKey(key.ReadModelKey)
            || key.SchemaVersion <= 0
            || key.PolicyVersion <= 0
            || !IsLowerHex(key.ParametersHash, 64)
            || !Enum.IsDefined(key.Audience)
            || !IsLowerKebab(key.TtlPolicyKey, MaximumTtlPolicyKeyLength)
            || !IsHostName(applicationNamespace)
            || !IsHostName(environment)
            || organizationId == Guid.Empty
            || actorId == Guid.Empty)
        {
            throw InvalidValue();
        }
    }

    private static bool IsLowerKebab(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
            return false;

        bool previousWasSeparator = true;
        foreach (char character in value)
        {
            if (IsLowerAlphaNumeric(character))
            {
                previousWasSeparator = false;
                continue;
            }

            if (character != '-' || previousWasSeparator)
                return false;
            previousWasSeparator = true;
        }
        return !previousWasSeparator;
    }

    private static bool IsReadModelKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumIdentifierLength)
            return false;

        bool previousWasSeparator = true;
        foreach (char character in value)
        {
            if (IsLowerAlphaNumeric(character))
            {
                previousWasSeparator = false;
                continue;
            }

            if (character is not ('.' or '-') || previousWasSeparator)
                return false;
            previousWasSeparator = true;
        }
        return !previousWasSeparator;
    }

    private static bool IsHostName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumIdentifierLength)
            return false;

        foreach (char character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsLowerHex(string? value, int exactLength)
    {
        if (value is null || value.Length != exactLength)
            return false;

        foreach (char character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, encoded.Length);
        hash.AppendData(encoded);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(encoded, value);
        hash.AppendData(encoded);
    }

    private static ArgumentException InvalidValue() => new(InvalidValueMessage);
}
