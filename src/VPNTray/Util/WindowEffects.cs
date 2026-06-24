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

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;          // rounded corners
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic backdrop

    /// <summary>
    /// Apply rounded corners + acrylic + shadow. Call once the native handle
    /// exists (i.e. from the window's <c>SourceInitialized</c> event).
    /// </summary>
    public static void Apply(Window window)
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
        }
        catch { /* best effort — keep the window working without effects */ }
    }
}
