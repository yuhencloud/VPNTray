using System.Text.Json;
using VPNTray.Config;
using VPNTray.Dependencies;

namespace VPNTray.Tests.Config;

public class VpnConfigTests
{
    private static VpnConfig FullyConfigured() => new()
    {
        Username = "first.last",
        Password = "Secret2021",
        TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "vpn.example.com",
        ServerCertPin = "pin-sha256:AAAA",
    };

    [Fact]
    public void IsConfigured_TrueWhenAllFiveRequiredFieldsPresent()
        => Assert.True(FullyConfigured().IsConfigured);

    [Theory]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("TotpSecret")]
    [InlineData("Gateway")]
    [InlineData("ServerCertPin")]
    public void IsConfigured_FalseWhenAnyRequiredFieldEmpty(string field)
    {
        var c = FullyConfigured();
        typeof(VpnConfig).GetProperty(field)!.SetValue(c, "");
        Assert.False(c.IsConfigured);
    }

    [Fact]
    public void Sanitized_StripsInvisibleAndControlChars()
    {
        var c = new VpnConfig { Username = "  ab\u200Bcd   ", TotpSecret = "EF\tGH" };
        var s = c.Sanitized();
        Assert.Equal("abcd", s.Username);
        Assert.Equal("EFGH", s.TotpSecret);
    }

    [Fact]
    public void Sanitized_FallsBackToBundledTools_WhenTheSavedPathIsGone()
    {
        // Must be a path that cannot exist on any machine — an earlier version of this test
        // pointed at C:\Program Files\VPNTray\... and started failing the moment the app was
        // actually installed there.
        var gone = Path.Combine(Path.GetTempPath(), "vpntray-uninstalled-" + Guid.NewGuid().ToString("N"));
        var c = new VpnConfig
        {
            OpenconnectPath = Path.Combine(gone, "openconnect.exe"),
            VpncScriptPath = Path.Combine(gone, "vpnc-script-win.js"),
        };

        var s = c.Sanitized();

        Assert.Equal(BundledBinaries.OpenconnectExe, s.OpenconnectPath);
        Assert.Equal(BundledBinaries.VpncScript, s.VpncScriptPath);
    }

    [Fact]
    public void Sanitized_KeepsTheSavedToolPath_WhenItStillExists()
    {
        var exe = Path.Combine(Path.GetTempPath(), "vpntray-oc-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "");
        try
        {
            var s = new VpnConfig { OpenconnectPath = exe }.Sanitized();
            Assert.Equal(exe, s.OpenconnectPath);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void Defaults_UseBundledPaths_AndSkipDnsOn()
    {
        var c = new VpnConfig();
        Assert.EndsWith("openconnect.exe", c.OpenconnectPath);
        Assert.True(c.SkipDnsModification);
    }

    [Fact]
    public void Deserialize_MigratesSchemaV1PasswordPrefix_IntoPassword()
    {
        const string v1Json = """
            {
              "Username": "first.last",
              "PasswordPrefix": "Secret2021",
              "TotpSecret": "GEZDGNBVGY3TQOJQ",
              "Gateway": "vpn.example.com",
              "ServerCertPin": "pin-sha256:AAAA",
              "SchemaVersion": 1
            }
            """;

        var c = JsonSerializer.Deserialize<VpnConfig>(v1Json)!;

        Assert.Equal("Secret2021", c.Password);
        Assert.True(c.IsConfigured);
    }

    [Fact]
    public void Deserialize_PrefersExplicitPassword_OverLegacyPrefix()
    {
        const string bothJson = """
            { "PasswordPrefix": "old", "Password": "new" }
            """;

        Assert.Equal("new", JsonSerializer.Deserialize<VpnConfig>(bothJson)!.Password);
    }

    [Fact]
    public void Serialize_WritesPassword_AndDropsTheLegacyPrefixKey()
    {
        var json = JsonSerializer.Serialize(FullyConfigured());

        Assert.Contains("\"Password\"", json);
        Assert.DoesNotContain("PasswordPrefix", json);
        Assert.Contains("\"SchemaVersion\":2", json);
    }
}
