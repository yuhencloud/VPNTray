namespace VPNTray.Core;

public sealed record OpenConnectHandshake(bool Connected, string? Reason);

public static class HandshakeAnalyzer
{
    private static readonly string[] SuccessMarkers =
        { "Connected as", "CSTP connected", "Established DTLS", "ESP session established" };

    private static readonly string[] FailureMarkers =
        { "Login failed", "authentication failure", "Certificate verification failure" };

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
        "Login failed" or "authentication failure" =>
            "Authentication failed — please check your TOTP secret or password prefix.",
        "Certificate verification failure" =>
            "Server certificate verification failed. The gateway cert may have rotated — update the pin in Settings.",
        _ => Tail(fullStderr, 200),
    };

    private static string Tail(string text, int chars)
    {
        text = text.Trim();
        return text.Length <= chars ? text : text[^chars..].Trim();
    }
}
