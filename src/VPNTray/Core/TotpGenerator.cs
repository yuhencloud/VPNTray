using System.Security.Cryptography;

namespace VPNTray.Core;

public class TotpException : Exception
{
    public TotpException(string message) : base(message) { }
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30s step) with an RFC 4648 Base32 decoder.</summary>
public static class TotpGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string secret, DateTimeOffset at = default, int digits = 6)
    {
        if (digits is < 1 or > 9) throw new TotpException("digits must be 1..9");
        if (at == default) at = DateTimeOffset.UtcNow;

        var key = Base32Decode(secret);
        long seconds = Math.Max(0, at.ToUnixTimeSeconds());
        ulong counter = (ulong)(seconds / 30);

        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        byte[] mac = HMACSHA1.HashData(key, counterBytes.ToArray());

        int offset = mac[^1] & 0x0f;
        int binary =
            ((mac[offset] & 0x7f) << 24) |
            ((mac[offset + 1] & 0xff) << 16) |
            ((mac[offset + 2] & 0xff) << 8) |
            (mac[offset + 3] & 0xff);

        int modulus = (int)Math.Pow(10, digits);
        int truncated = binary % modulus;
        return truncated.ToString().PadLeft(digits, '0');
    }

    public static byte[] Base32Decode(string input)
    {
        var cleaned = input.ToUpperInvariant()
            .Replace("=", "")
            .Replace(" ", "");

        int bits = 0;
        uint buffer = 0;
        var output = new List<byte>(cleaned.Length * 5 / 8 + 1);

        foreach (char c in cleaned)
        {
            int value = Alphabet.IndexOf(c);
            if (value < 0) throw new TotpException($"invalid Base32 character '{c}'");
            buffer = (buffer << 5) | (uint)value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return output.ToArray();
    }
}
