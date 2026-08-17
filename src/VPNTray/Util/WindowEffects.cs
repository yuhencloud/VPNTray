using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VPNTray.Util;

/// <summary>
/// Rounded corners (and the standard shadow that comes with them) for the borderless window,
/// via the one DWM attribute that is safe to use from WPF. Best-effort — silently no-ops on
/// Windows 10, which leaves square corners and nothing worse.
///
/// Deliberately nothing else. This used to also bolt a Win11 acrylic backdrop onto the window
/// (DWMWA_SYSTEMBACKDROP_TYPE + DwmExtendFrameIntoClientArea over a transparent background),
/// which is a WinUI-era composition feature with no supported WPF path. It made every pixel
/// depend on DWM state that a monitor crossing silently drops — the window went WHITE mid-drag —
/// and each fix needed another message hook (WM_DPICHANGED, WM_EXITSIZEMOVE, WM_DISPLAYCHANGE,
/// WM_WINDOWPOSCHANGED with monitor tracking...). An opaque background costs the frosted-glass
/// look and buys all of that back: WPF always paints the full window, and DPI/multi-monitor
/// behaviour is entirely the framework's problem, which it handles. The corner preference is a
/// sticky per-window attribute, not per-frame state, so it needs no re-applying.
/// </summary>
public static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void Attach(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { /* best effort — square corners are fine */ }
    }
}
