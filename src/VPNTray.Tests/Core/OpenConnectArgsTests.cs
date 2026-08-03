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

    private const string TokenPath = @"C:\temp\vpntray-abc.tok";

    [Fact]
    public void Build_IncludesUserPinGatewayAndStdinFlag()
    {
        var args = OpenConnectArgs.Build(Cfg(), TokenPath);
        Assert.Contains("--user", args);
        Assert.Contains("first.last", args);
        Assert.Contains("--passwd-on-stdin", args);
        Assert.Contains("--servercert", args);
        Assert.Contains("pin-sha256:AAAA", args);
        Assert.Equal("vpn.example.com", args[^1]); // gateway is last positional
    }

    [Fact]
    public void Build_EnablesTotpSoftToken_ForTheSecondAuthStage()
    {
        var args = OpenConnectArgs.Build(Cfg(), TokenPath);
        var list = args.ToList();

        int mode = list.IndexOf("--token-mode");
        Assert.NotEqual(-1, mode);
        Assert.Equal("totp", args[mode + 1]);

        int secret = list.IndexOf("--token-secret");
        Assert.NotEqual(-1, secret);
        Assert.Equal("@" + TokenPath, args[secret + 1]); // @FILE, never the secret itself
    }

    [Fact]
    public void Build_NeverPutsTheTotpSecretOnTheCommandLine()
    {
        var cfg = Cfg();
        cfg.TotpSecret = "GEZDGNBVGY3TQOJQ";
        var args = OpenConnectArgs.Build(cfg, TokenPath);
        Assert.DoesNotContain(args, a => a.Contains("GEZDGNBVGY3TQOJQ", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IsNonInteractive_SoUnexpectedFormsFailInsteadOfHanging()
        => Assert.Contains("--non-inter", OpenConnectArgs.Build(Cfg(), TokenPath));

    [Fact]
    public void TokenSecretFileContent_IsBase32Prefixed()
        => Assert.Equal("base32:GEZDGNBVGY3TQOJQ", OpenConnectArgs.TokenSecretFileContent("GEZDGNBVGY3TQOJQ"));

    [Theory]
    [InlineData("GEZD GNBV GY3T QOJQ")] // grouping spaces — openconnect rejects these outright
    [InlineData("gezdgnbvgy3tqojq")]    // lower case
    [InlineData("GEZDGNBVGY3TQOJQ=")]   // padding
    public void TokenSecretFileContent_NormalizesWhatOpenconnectWouldReject(string pasted)
        => Assert.Equal("base32:GEZDGNBVGY3TQOJQ", OpenConnectArgs.TokenSecretFileContent(pasted));

    [Fact]
    public void TokenSecretFileContent_HasNoTrailingNewline()
        => Assert.DoesNotContain("\n", OpenConnectArgs.TokenSecretFileContent("GEZDGNBVGY3TQOJQ"));

    [Fact]
    public void Build_UsesConfiguredScript_WhenSkipDnsOff()
    {
        var args = OpenConnectArgs.Build(Cfg(), TokenPath);
        int i = args.ToList().IndexOf("--script");
        Assert.Equal(@"C:\app\Resources\vpnc-script-win.js", args[i + 1]);
    }

    [Fact]
    public void Build_UsesNoDnsScript_WhenSkipDnsOn()
    {
        var cfg = Cfg();
        cfg.SkipDnsModification = true;
        var args = OpenConnectArgs.Build(cfg, TokenPath);
        int i = args.ToList().IndexOf("--script");
        Assert.EndsWith("vpnc-script-win--no-dns.js", args[i + 1]);
    }
}
