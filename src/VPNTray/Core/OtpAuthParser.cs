namespace VPNTray.Core;

/// <summary>
/// Extracts the Base32 TOTP secret from text decoded out of a QR code —
/// either a full otpauth:// URI or a bare Base32 string.
/// </summary>
public static class OtpAuthParser
{
    /// <summary>Returns the normalized Base32 secret, or null if none found.</summary>
    public static string? TryExtractSecret(string? decodedText)
    {
        if (string.IsNullOrWhiteSpace(decodedText)) return null;
        var text = decodedText.Trim();

        if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            return SecretFromOtpAuthUri(text);

        var bare = Normalize(text);
        return IsValidBase32(bare) ? bare : null;
    }

    private static string? SecretFromOtpAuthUri(string uri)
    {
        int q = uri.IndexOf('?');
        if (q < 0 || q == uri.Length - 1) return null;

        foreach (var pair in uri[(q + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals("secret", StringComparison.OrdinalIgnoreCase)) continue;

            var secret = Normalize(Uri.UnescapeDataString(pair[(eq + 1)..]));
            return IsValidBase32(secret) ? secret : null;
        }
        return null;
    }

    /// <summary>Uppercase, strip spaces/dashes and trailing '=' padding.</summary>
    private static string Normalize(string value) =>
        value.ToUpperInvariant().Replace(" ", "").Replace("-", "").TrimEnd('=');

    private static bool IsValidBase32(string value)
    {
        // Real TOTP secrets are 80+ bits; anything shorter is likely a false positive
        // (e.g. a QR code containing an unrelated short word).
        if (value.Length < 16) return false;
        try
        {
            TotpGenerator.Base32Decode(value);
            return true;
        }
        catch (TotpException)
        {
            return false;
        }
    }
}
