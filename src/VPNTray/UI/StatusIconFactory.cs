using VPNTray.Core;

namespace VPNTray.UI;

public static class StatusIconFactory
{
    public static string DotColorName(VpnStatus status) => status switch
    {
        VpnStatus.Connected => "green",
        VpnStatus.Connecting => "orange",
        VpnStatus.Failed => "red",
        _ => "gray",
    };

    private static string FileFor(VpnStatus status) => status switch
    {
        VpnStatus.Connected => "connected",
        VpnStatus.Connecting => "connecting",
        VpnStatus.Failed => "failed",
        _ => "disconnected",
    };

    /// <summary>Pack URI for the tray icon resource of the given status.</summary>
    public static string IconResourcePath(VpnStatus status)
        => $"pack://application:,,,/Resources/icons/{FileFor(status)}.ico";
}
