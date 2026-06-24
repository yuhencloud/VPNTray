using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class OpenConnectArgsTests
{
    private static VpnConfig Cfg() => new()
    {
        Username = "first.last",
        Gateway = "vpn.example.com",
        ServerCertPin = "pin-sha256:AAAA",
        VpncScriptPath = @"C:\app\Resources\vpnc-script-win.js",
        SkipDnsModification = false,
    };

    [Fact]
    public void Build_IncludesUserPinGatewayAndStdinFlag()
    {
        var args = OpenConnectArgs.Build(Cfg());
        Assert.Contains("--user", args);
        Assert.Contains("first.last", args);
        Assert.Contains("--passwd-on-stdin", args);
        Assert.Contains("--servercert", args);
        Assert.Contains("pin-sha256:AAAA", args);
        Assert.Equal("vpn.example.com", args[^1]); // gateway is last positional
    }

    [Fact]
    public void Build_UsesConfiguredScript_WhenSkipDnsOff()
    {
        var args = OpenConnectArgs.Build(Cfg());
        int i = args.ToList().IndexOf("--script");
        Assert.Equal(@"C:\app\Resources\vpnc-script-win.js", args[i + 1]);
    }

    [Fact]
    public void Build_UsesNoDnsScript_WhenSkipDnsOn()
    {
        var cfg = Cfg();
        cfg.SkipDnsModification = true;
        var args = OpenConnectArgs.Build(cfg);
        int i = args.ToList().IndexOf("--script");
        Assert.EndsWith("vpnc-script-win--no-dns.js", args[i + 1]);
    }
}
