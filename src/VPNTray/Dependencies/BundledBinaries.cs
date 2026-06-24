using System.IO;

namespace VPNTray.Dependencies;

/// <summary>Resolves paths to the native binaries bundled in the app's Resources folder.</summary>
public static class BundledBinaries
{
    public static string BaseDir => Path.Combine(AppContext.BaseDirectory, "Resources");

    public static string OpenconnectExe => Path.Combine(BaseDir, "openconnect.exe");
    public static string WintunDll => Path.Combine(BaseDir, "wintun.dll");
    public static string VpncScript => Path.Combine(BaseDir, "vpnc-script-win.js");
    public static string VpncScriptNoDns => Path.Combine(BaseDir, "vpnc-script-win--no-dns.js");
}
