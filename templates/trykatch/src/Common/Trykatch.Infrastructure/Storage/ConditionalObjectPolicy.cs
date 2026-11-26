using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

internal static class ConditionalObjectPolicy
{
    internal const string ManagedPrefix = "_module-uploads/v1/";
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    internal static bool IsValidKey(string? key)
    {
        if (key is null || key.Length > 500 || !key.StartsWith(ManagedPrefix, StringComparison.Ordinal)) return false;
        foreach (string segment in key[ManagedPrefix.Length..].Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment.Contains("..", StringComparison.Ordinal)) return false;
            foreach (char character in segment)
                if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')) return false;
        }
        return true;
    }

    internal static bool IsReservedRawKey(string key) => key.Equals("_module-uploads", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("_module-uploads/", StringComparison.OrdinalIgnoreCase);

    internal static bool TryIdentity(ObjectWriteIdentity? identity, out ObjectWriteIdentity normalized)
    {
        normalized = null!;
        if (identity is null || identity.OperationId == Guid.Empty || identity.OriginalLength <= 0
            || !IsHash(identity.Sha256) || !IsHash(identity.MetadataFingerprint)) return false;
        normalized = identity with { Sha256 = identity.Sha256.ToLowerInvariant(), MetadataFingerprint = identity.MetadataFingerprint.ToLowerInvariant() };
        return true;
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static bool IsValidContentType(string? contentType) => contentType is { Length: > 0 and <= 255 }
        && !contentType.Any(char.IsControl) && MediaTypeHeaderValue.TryParse(contentType, out _);

    internal static bool IsValidRevision(ObjectStorageRevision? revision) => revision is null
        || revision.Value is { Length: > 0 and <= 512 } && revision.Value.All(character => character is >= '!' and <= '~');

    internal static bool SameIdentity(ObjectWriteIdentity? left, ObjectWriteIdentity? right) => left is not null && right is not null
        && left.OperationId == right.OperationId && left.OriginalLength == right.OriginalLength
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.MetadataFingerprint, right.MetadataFingerprint, StringComparison.OrdinalIgnoreCase);

    internal static ConditionalObjectWriteResult Existing(ConditionalObjectStat snapshot, ObjectWriteIdentity identity) =>
        new(snapshot.State is ConditionalObjectState.Provisional or ConditionalObjectState.Cancellation
                && SameIdentity(snapshot.Identity, identity)
                    ? ConditionalObjectWriteDisposition.AlreadyExists : ConditionalObjectWriteDisposition.Conflict,
            snapshot);

    internal static string NamespaceFingerprint(string kind, params string[] destination) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { kind }.Concat(destination))));
}
