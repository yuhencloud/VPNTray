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
}
