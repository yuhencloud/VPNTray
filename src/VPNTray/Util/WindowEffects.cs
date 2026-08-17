using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VPNTray.Util;

/// <summary>
/// Windows 11 window effects via DWM: rounded corners, an acrylic backdrop
/// (frosted glass), and a drop shadow. Best-effort — silently no-ops on older
/// Windows or if the DWM calls aren't supported.
/// </summary>
public static class WindowEffects
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;          // rounded corners
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic backdrop

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Applies the effects as soon as the native handle exists, and re-applies them on every
    /// DPI change for the life of the window.
    ///
    /// The re-apply is not belt-and-braces. <see cref="DwmExtendFrameIntoClientArea"/>
    /// establishes a glass region bound to the window's current frame, and a per-monitor DPI
    /// change resizes that frame — so the region, the rounded corners and the backdrop are all
    /// dropped. This window has no background of its own (WindowStyle=None over a transparent
    /// Background, precisely so the acrylic shows through), so what is left on screen is
    /// whatever happened to be in the buffer: a white window that only redraws where the
    /// pointer passes over it.
    /// </summary>
    public static void Attach(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);

        // Deferred: WPF resizes the window in response to WM_DPICHANGED, and re-establishing
        // the glass region before that resize would just lose it again.
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(() => Apply(window));
    }

    /// <summary>
    /// Apply rounded corners + acrylic + shadow. Requires the native handle to exist, which
    /// <see cref="Attach"/> is responsible for — go through that.
    /// </summary>
    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // Extend the frame across the whole window: lets the acrylic backdrop
            // show through translucent content and gives the window a drop shadow.
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // Make DWM recompute the non-client frame now. Everything above is frame state, and
            // without this nudge it only takes hold at the next natural frame change — which
            // after a DPI change can mean not until the user happens to move the pointer over
            // the window. No move, no resize, no z-order or focus change: only the frame.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            window.InvalidateVisual();
        }
        catch { /* best effort — keep the window working without effects */ }
    }
}
