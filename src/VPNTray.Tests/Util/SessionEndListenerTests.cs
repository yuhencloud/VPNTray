using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class SessionEndListenerTests
{
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    [Fact]
    public void Classify_QueryEndSession_IsTheAskWeMustAgreeTo()
        => Assert.Equal(
            SessionEndSignal.MayClose,
            SessionEndListener.Classify(WM_QUERYENDSESSION, IntPtr.Zero));

    [Fact]
    public void Classify_EndSessionWithTrue_MeansExitNow()
        => Assert.Equal(
            SessionEndSignal.Close,
            SessionEndListener.Classify(WM_ENDSESSION, new IntPtr(1)));

    /// <summary>
    /// A cancelled shutdown arrives as WM_ENDSESSION with wParam FALSE. Treating that as the cue
    /// to exit would take the app down every time somebody backed out of a restart.
    /// </summary>
    [Fact]
    public void Classify_EndSessionWithFalse_IsACancelledShutdown_NotOurCue()
        => Assert.Equal(
            SessionEndSignal.None,
            SessionEndListener.Classify(WM_ENDSESSION, IntPtr.Zero));

    /// <summary>
    /// An installer asking politely sends WM_CLOSE, and this window is the only thing it has to
    /// send it to. Ignoring it leaves the installer nothing but a terminate, which skips cleanup.
    /// </summary>
    [Fact]
    public void Classify_Close_IsAPoliteRequestToGoAway()
        => Assert.Equal(SessionEndSignal.Close, SessionEndListener.Classify(WM_CLOSE, IntPtr.Zero));

    [Theory]
    [InlineData(0x0002)]  // WM_DESTROY
    [InlineData(0x0018)]  // WM_SHOWWINDOW
    [InlineData(0x0200)]  // WM_MOUSEMOVE
    public void Classify_AnythingElse_IsIgnored(int message)
        => Assert.Equal(SessionEndSignal.None, SessionEndListener.Classify(message, new IntPtr(1)));

    /// <summary>installer/product.wxs matches this window by title; drift breaks the fallback.</summary>
    [Fact]
    public void WindowTitle_IsWhatTheInstallerLooksFor()
        => Assert.Equal("VPNTray.SessionSink", SessionEndListener.WindowTitle);
}
