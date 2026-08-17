using System.Windows.Interop;

namespace VPNTray.Util;

/// <summary>What a window message means for the app's lifetime.</summary>
public enum SessionEndSignal
{
    None,

    /// <summary>Asked whether we can close. Answering yes is what lets the requester proceed.</summary>
    MayClose,

    /// <summary>Told the session really is ending. Exit now.</summary>
    Close,
}

/// <summary>
/// A top-level window that is never shown, whose only job is to answer WM_QUERYENDSESSION.
///
/// Restart Manager — which is what Windows Installer uses to clear the way before replacing a
/// running program's files — asks a GUI process to close by sending that message to its top-level
/// windows. VPNTray spends most of its life as a tray icon with no window open at all, so there
/// was nothing to ask: RM concluded it could not close the application, and the install stopped
/// on "please ensure the applications holding files in use are closed". WPF's
/// <c>Application.SessionEnding</c> never fired either, for the same reason — it has no window of
/// its own to receive on.
///
/// Answering properly is better than being killed: the app gets to remove its NRPT rules and stop
/// openconnect on the way out, which a terminate skips.
/// </summary>
public sealed class SessionEndListener : IDisposable
{
    /// <summary>
    /// The window's title. WiX's util:CloseApplication finds its target by enumerating windows,
    /// which is why it did nothing at all while this app was tray-only — there was no window to
    /// find. installer/product.wxs matches on this string, so the two have to stay in step.
    /// </summary>
    public const string WindowTitle = "VPNTray.SessionSink";

    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    // Tool window, and no WS_VISIBLE: a real top-level window for EnumWindows to find, but not
    // one that shows up on screen, in the taskbar, or in Alt-Tab.
    private const int WS_EX_TOOLWINDOW = 0x0080;

    private readonly Action _onClose;
    private readonly HwndSource _source;

    public SessionEndListener(Action onClose)
    {
        _onClose = onClose;
        _source = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _source.AddHook(Hook);
    }

    /// <summary>
    /// Reads a message without needing a window, so the protocol can be tested on its own.
    /// WM_ENDSESSION arrives with wParam FALSE when a shutdown was cancelled — not our cue.
    ///
    /// WM_CLOSE is here because an installer asking politely sends exactly that, and this window
    /// is the only thing it can send it to. Ignoring it would leave the installer with nothing
    /// left but to terminate the process, which skips the split-DNS cleanup.
    /// </summary>
    public static SessionEndSignal Classify(int message, IntPtr wParam) => message switch
    {
        WM_QUERYENDSESSION => SessionEndSignal.MayClose,
        WM_ENDSESSION when wParam != IntPtr.Zero => SessionEndSignal.Close,
        WM_CLOSE => SessionEndSignal.Close,
        _ => SessionEndSignal.None,
    };

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (Classify(message, wParam))
        {
            case SessionEndSignal.MayClose:
                AppLogger.Shared.Info("session end query: agreeing to close");
                handled = true;
                return new IntPtr(1);   // TRUE — go ahead

            case SessionEndSignal.Close:
                AppLogger.Shared.Info("session end confirmed: shutting down");
                handled = true;
                _onClose();
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        try { _source.RemoveHook(Hook); } catch { /* already torn down */ }
        try { _source.Dispose(); } catch { /* already torn down */ }
    }
}
