using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Trykatch.Application.Realtime;

namespace Trykatch.Infrastructure.Realtime;

internal sealed class RealtimeCursorProtector : IRealtimeCursorProtector
{
    private const int BindingLength = 81;
    private const int PayloadLength = BindingLength + sizeof(long);
    private readonly IDataProtector protector;

    public RealtimeCursorProtector(IDataProtectionProvider provider) =>
        protector = provider.CreateProtector("Trykatch.Realtime.Cursor.v1");

    public string Protect(RealtimeHistoryScope scope, long position)
    {
        if (!ValidScope(scope)) throw new ArgumentException("A current realtime history scope is required.", nameof(scope));
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        byte[] payload = Binding(scope);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(BindingLength), position);
        return WebEncoders.Base64UrlEncode(protector.Protect(payload));
    }

    public bool TryRead(string? cursor, RealtimeHistoryScope scope, out long position)
    {
        position = 0;
        if (!ValidScope(scope) || string.IsNullOrEmpty(cursor) || cursor.Length > 2048
            || cursor.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        try
        {
            byte[] payload = protector.Unprotect(WebEncoders.Base64UrlDecode(cursor));
            if (payload.Length != PayloadLength
                || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(0, BindingLength), Binding(scope).AsSpan(0, BindingLength)))
                return false;
            long decoded = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(BindingLength));
            if (decoded < 0) return false;
            position = decoded;
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    private static bool ValidScope(RealtimeHistoryScope scope) => scope is not null
        && scope.OrganizationId != Guid.Empty && scope.ActorId != Guid.Empty && scope.WorkspaceGeneration != Guid.Empty
        && scope.SessionHash is { Length: 64 }
        && scope.SessionHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Binding(RealtimeHistoryScope scope)
    {
        byte[] payload = new byte[PayloadLength];
        payload[0] = 1;
        scope.OrganizationId.TryWriteBytes(payload.AsSpan(1, 16));
        scope.ActorId.TryWriteBytes(payload.AsSpan(17, 16));
        scope.WorkspaceGeneration.TryWriteBytes(payload.AsSpan(33, 16));
        Convert.FromHexString(scope.SessionHash).CopyTo(payload, 49);
        return payload;
    }
}
