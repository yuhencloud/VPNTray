using System.Text.RegularExpressions;

namespace VPNTray.Core;

public sealed record OpenConnectHandshake(bool Connected, string? Reason);

public static class HandshakeAnalyzer
{
    private const string PinMismatchMarker = "match server's certificate";

    private const string TlsDroppedMarker = "SSL connection failure";
    private const string HttpsUnreachableMarker = "Failed to open HTTPS connection";

    private const string GatewayUnreachableReason =
        "Could not open a secure connection to the VPN gateway — it dropped the connection " +
        "before login. Try again; if it keeps failing, the gateway may be having trouble.";

    /// <summary>The reason for a rejected login. Public so callers can tell it from transient failures.</summary>
    public const string AuthFailedReason =
        "Authentication failed — check the password and TOTP secret, and the OTP mode in Settings.";

    private static readonly string[] SuccessMarkers =
        { "Connected as", "CSTP connected", "Established DTLS", "ESP session established" };

    /// <remarks>
    /// Order matters: the first marker found wins. Certificate problems must come before
    /// "Failed to complete authentication", because openconnect prints that line after a
    /// rejected cert too — matching it first would blame the user's password for what is
    /// really a stale cert pin.
    /// </remarks>
    private static readonly string[] FailureMarkers =
    {
        "Login failed", "authentication failure",
        PinMismatchMarker, "Certificate verification failure", "Error in the certificate",
        // Transport failures, after the cert markers (a bad cert also prints them) and before
        // the auth give-up line (printed after them too). No credentials were ever sent, so
        // calling this an auth failure would blame the password and stop the auto-reconnect
        // that a flaky gateway node needs.
        TlsDroppedMarker, HttpsUnreachableMarker,
        // Emitted when auth gives up — including when --non-inter aborts on a form we
        // cannot answer (e.g. the gateway also wants an auth group or a PIN change).
        "Failed to complete authentication",
    };

    private static readonly Regex ServerPin = new(@"pin-sha256:[A-Za-z0-9+/]+=*", RegexOptions.Compiled);

    /// <summary>
    /// Decide the handshake outcome from the current stderr snapshot, or return
    /// null to keep waiting. Caller supplies liveness facts it alone can observe.
    /// </summary>
    public static OpenConnectHandshake? Evaluate(string stderr, bool processAlive, bool gracePassed, bool childConfirmedAlive)
    {
        foreach (var marker in FailureMarkers)
            if (stderr.Contains(marker, StringComparison.Ordinal))
                return new OpenConnectHandshake(false, MapMarker(marker, stderr));

        if (!processAlive)
        {
            var tail = Tail(stderr, 200);
            return new OpenConnectHandshake(false, tail.Length == 0 ? "openconnect exited before handshake" : tail);
        }

        bool keywordHit = SuccessMarkers.Any(m => stderr.Contains(m, StringComparison.Ordinal));
        if ((keywordHit || gracePassed) && childConfirmedAlive)
            return new OpenConnectHandshake(true, null);

        return null; // keep waiting
    }

    public static string FriendlyReason(string fullStderr)
    {
        foreach (var marker in FailureMarkers)
            if (fullStderr.Contains(marker, StringComparison.Ordinal))
                return MapMarker(marker, fullStderr);
        var tail = Tail(fullStderr, 200);
        return tail.Length == 0 ? "Handshake timeout" : tail;
    }

    private static string MapMarker(string marker, string fullStderr) => marker switch
    {
        "Login failed" or "authentication failure" or "Failed to complete authentication" =>
            AuthFailedReason,
        PinMismatchMarker => PinMismatchReason(fullStderr),
        TlsDroppedMarker or HttpsUnreachableMarker => GatewayUnreachableReason,
        "Certificate verification failure" or "Error in the certificate" =>
            "Server certificate verification failed. The gateway cert may have rotated — update Server cert pin in Settings.",
        _ => Tail(fullStderr, 200),
    };

    /// <summary>
    /// openconnect prints the pin it actually saw on the mismatch line, which is exactly what
    /// the user needs to paste into Settings — so hand it over instead of making them dig
    /// through the log for it.
    /// </summary>
    private static string PinMismatchReason(string fullStderr)
    {
        const string baseMessage = "Server cert pin does not match this gateway.";
        var line = fullStderr
            .Split('\n')
            .FirstOrDefault(l => l.Contains(PinMismatchMarker, StringComparison.Ordinal));

        var pin = line is null ? Match.Empty : ServerPin.Match(line);
        return pin.Success
            ? $"{baseMessage} The gateway presents {pin.Value} — update Server cert pin in Settings."
            : $"{baseMessage} Update Server cert pin in Settings.";
    }

    private static string Tail(string text, int chars)
    {
        text = text.Trim();
        return text.Length <= chars ? text : text[^chars..].Trim();
    }
}
