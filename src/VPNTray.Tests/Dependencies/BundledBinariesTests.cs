using VPNTray.Dependencies;

namespace VPNTray.Tests.Dependencies;

public class BundledBinariesTests
{
    [Fact]
    public void Paths_AreUnderResources_AndNamedCorrectly()
    {
        Assert.EndsWith(Path.Combine("Resources", "openconnect.exe"), BundledBinaries.OpenconnectExe);
        Assert.EndsWith(Path.Combine("Resources", "wintun.dll"), BundledBinaries.WintunDll);
        Assert.EndsWith(Path.Combine("Resources", "vpnc-script-win.js"), BundledBinaries.VpncScript);
        Assert.EndsWith(Path.Combine("Resources", "vpnc-script-win--no-dns.js"), BundledBinaries.VpncScriptNoDns);
    }
}
