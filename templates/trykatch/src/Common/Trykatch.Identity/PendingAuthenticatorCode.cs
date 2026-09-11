using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Trykatch.Identity;

// RFC 6238, interoperable with Identity's six-digit SHA-1 authenticator provider.
// This verifies a pending key without writing it into the active Identity store.
internal static class PendingAuthenticatorCode
{
    public static bool Verify(string key, string code, DateTimeOffset now)
    {
        if (code.Length != 6 || code.Any(character => character is < '0' or > '9')) return false;
        byte[] bytes = DecodeBase32(key);
        Span<byte> counter = stackalloc byte[8];
        long step = now.ToUnixTimeSeconds() / 30;
        for (int drift = -2; drift <= 2; drift++)
        {
            BinaryPrimitives.WriteInt64BigEndian(counter, step + drift);
#pragma warning disable CA5350 // Required for RFC 6238/ASP.NET Core Identity interoperability.
            byte[] hash = HMACSHA1.HashData(bytes, counter);
#pragma warning restore CA5350
            int offset = hash[^1] & 15;
            int value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue;
            if ((value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture) == code) return true;
        }
        return false;
    }

    private static byte[] DecodeBase32(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> decoded = [];
        int buffer = 0;
        int bits = 0;
        foreach (char character in key)
        {
            int digit = alphabet.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0) throw new FormatException("Invalid pending authenticator key.");
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                decoded.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return [.. decoded];
    }
}
