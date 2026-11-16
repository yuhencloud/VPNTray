using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class HandshakeAnalyzerTests
{
    [Fact]
    public void Evaluate_SuccessMarker_WhenAliveAndChildConfirmed_ReturnsConnected()
    {
        var h = HandshakeAnalyzer.Evaluate("... CSTP connected ...", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.NotNull(h);
        Assert.True(h!.Connected);
    }

    [Fact]
    public void Evaluate_AuthFailureMarker_ReturnsFailedWithFriendlyReason()
    {
        var h = HandshakeAnalyzer.Evaluate("Login failed: bad creds", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.NotNull(h);
        Assert.False(h!.Connected);
        Assert.Contains("Authentication failed", h.Reason);
    }

    [Fact]
    public void Evaluate_AuthGaveUpMarker_ReturnsFailedWithFriendlyReason()
    {
        var h = HandshakeAnalyzer.Evaluate("Failed to complete authentication",
            processAlive: true, gracePassed: true, childConfirmedAlive: true);
        Assert.NotNull(h);
        Assert.False(h!.Connected);
        Assert.Contains("Authentication failed", h.Reason);
    }

    // Shape of a real stale-pin failure, with the gateway host and pin replaced by
    // placeholders. The point is that openconnect prints BOTH a cert complaint and
    // "Failed to complete authentication", so the cert cause must win — otherwise the
    // app tells the user to check a password that was never the problem.
    private const string GatewayPin = "pin-sha256:9Xk2QpLmR4tZvN8yB1cE7wJhF0sD6aG3uK5oI2nT4bQ=";

    private const string PinMismatchStderr = $"""
        None of the 1 fingerprint(s) specified via --servercert match server's certificate: {GatewayPin}
        SSL connection failure: Error in the certificate.
        Failed to open HTTPS connection to vpn.example.com
        Failed to complete authentication
        """;

    [Fact]
    public void Evaluate_PinMismatch_BlamesTheCertPin_NotThePassword()
    {
        var h = HandshakeAnalyzer.Evaluate(PinMismatchStderr,
            processAlive: false, gracePassed: false, childConfirmedAlive: false);

        Assert.NotNull(h);
        Assert.False(h!.Connected);
        Assert.Contains("Server cert pin", h.Reason);
        Assert.DoesNotContain("password", h.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOTP", h.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_PinMismatch_SurfacesTheGatewaysActualPin()
    {
        var h = HandshakeAnalyzer.Evaluate(PinMismatchStderr, false, false, false);
        Assert.Contains(GatewayPin, h!.Reason);
    }

    [Fact]
    public void FriendlyReason_PinMismatch_AlsoBlamesTheCertPin()
        => Assert.Contains("Server cert pin", HandshakeAnalyzer.FriendlyReason(PinMismatchStderr));

    [Fact]
    public void Evaluate_CertFailureMarker_MentionsCertPin()
    {
        var h = HandshakeAnalyzer.Evaluate("Certificate verification failure", true, false, true);
        Assert.Contains("certificate", h!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ProcessExitedWithNoMarker_ReturnsFailed()
    {
        var h = HandshakeAnalyzer.Evaluate("some random output", processAlive: false, gracePassed: false, childConfirmedAlive: false);
        Assert.NotNull(h);
        Assert.False(h!.Connected);
    }

    [Fact]
    public void Evaluate_GracePassedAndAlive_ReturnsConnected()
    {
        var h = HandshakeAnalyzer.Evaluate("", processAlive: true, gracePassed: true, childConfirmedAlive: true);
        Assert.True(h!.Connected);
    }

    [Fact]
    public void Evaluate_StillConnecting_ReturnsNull()
    {
        var h = HandshakeAnalyzer.Evaluate("negotiating...", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.Null(h);
    }

    // Shape of a real failure against a gateway node that accepts TCP but drops the TLS
    // handshake, host replaced by a placeholder. openconnect still ends with "Failed to
    // complete authentication", although no credentials were ever sent.
    private const string TlsDroppedStderr = """
        SSL connection failure: The TLS connection was non-properly terminated.
        Failed to open HTTPS connection to vpn.example.com
        Failed to complete authentication
        """;

    [Fact]
    public void Evaluate_TlsDroppedBeforeLogin_IsNotAnAuthFailure()
    {
        var h = HandshakeAnalyzer.Evaluate(TlsDroppedStderr,
            processAlive: false, gracePassed: false, childConfirmedAlive: false);

        Assert.NotNull(h);
        Assert.False(h!.Connected);
        Assert.NotEqual(HandshakeAnalyzer.AuthFailedReason, h.Reason);
        Assert.Contains("gateway", h.Reason);
        Assert.DoesNotContain("password", h.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendlyReason_TlsDroppedBeforeLogin_IsNotAnAuthFailure()
        => Assert.NotEqual(HandshakeAnalyzer.AuthFailedReason, HandshakeAnalyzer.FriendlyReason(TlsDroppedStderr));
}
