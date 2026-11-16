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
    public void Defaults_UseBundledPaths_AndLeaveSystemDnsAlone()
    {
        var c = new VpnConfig();
        Assert.EndsWith("openconnect.exe", c.OpenconnectPath);
        // Public-DNS mode by default. The fleet's internal resolver answers several public
        // names with unroutable in-cloud addresses, which breaks downloads out of the box;
        // not resolving *.corp names is the lesser default evil, and switching to internal
        // DNS is one radio button. See the property's own doc comment.
        Assert.True(c.SkipDnsModification);
        // Empty, not a guess: which public names an internal resolver breaks is specific to
        // the gateway, and a shipped suffix installs a machine-wide rule nobody asked for.
        Assert.Empty(c.SplitDnsPublicNamespaces);
        Assert.Empty(c.SplitDnsPublicServers);
        // Same reasoning in the other direction: which internal suffixes exist is the
        // deployment's business, and each one shipped would be a machine-wide NRPT rule.
        Assert.Empty(c.SplitDnsInternalNamespaces);
    }

    /// <summary>
    /// Migration stamps the version and nothing else. It used to force SkipDnsModification off
    /// for pre-v3 files; with the default back to public DNS, an explicit value on disk — either
    /// way, whoever wrote it — is the user's setting, not something to overrule on load.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Migrate_StampsTheVersion_AndPreservesTheDnsChoice(bool skipDns)
    {
        var c = new VpnConfig { SkipDnsModification = skipDns, SchemaVersion = 2 };

        c.Migrate();

        Assert.Equal(skipDns, c.SkipDnsModification);
        Assert.Equal(VpnConfig.CurrentSchemaVersion, c.SchemaVersion);
    }

    [Fact]
    public void Sanitized_CleansBypassDomains_AndDropsBlankEntries()
    {
        var c = new VpnConfig
        {
            SplitDnsPublicNamespaces = new List<string> { "  .a.com ", "", "\u200B", ".b\u200B.com" },
            SplitDnsInternalNamespaces = new List<string> { " .corp.a.com ", "\u200B" },
        };

        var s = c.Sanitized();

        Assert.Equal(new[] { ".a.com", ".b.com" }, s.SplitDnsPublicNamespaces);
        Assert.Equal(new[] { ".corp.a.com" }, s.SplitDnsInternalNamespaces);
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
        Assert.Contains("\"SchemaVersion\":3", json);
    }

    [Fact]
    public void TunnelSettingsMatch_WhenOnlyTheExceptionListsDiffer()
    {
        var before = FullyConfigured();
        var after = FullyConfigured();
        after.SplitDnsPublicNamespaces = new List<string> { ".example.com", ".other.net" };
        after.SplitDnsPublicServers = new List<string> { "9.9.9.9" };
        // Included deliberately: adding an internal suffix mid-session must refresh the NRPT
        // rules, not rebuild the tunnel — a reconnect would need a TOTP from a window the
        // previous login just spent.
        after.SplitDnsInternalNamespaces = new List<string> { ".corp.example.com" };

        Assert.True(before.TunnelSettingsMatch(after));
    }

    [Fact]
    public void TunnelSettingsMatch_IsFalseWhenTheDnsModeDiffers()
    {
        var before = FullyConfigured();
        var after = FullyConfigured();
        after.SkipDnsModification = !before.SkipDnsModification;

        Assert.False(before.TunnelSettingsMatch(after));
    }

    [Theory]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("TotpSecret")]
    [InlineData("Gateway")]
    [InlineData("ServerCertPin")]
    [InlineData("OpenconnectPath")]
    [InlineData("VpncScriptPath")]
    public void TunnelSettingsMatch_IsFalseWhenAnySpawnTimeFieldDiffers(string property)
    {
        var before = FullyConfigured();
        var after = FullyConfigured();
        var prop = typeof(VpnConfig).GetProperty(property)!;
        prop.SetValue(after, (string)prop.GetValue(before)! + "-changed");

        Assert.False(before.TunnelSettingsMatch(after));
    }

    [Fact]
    public void AppendOtpToPassword_DefaultsOn()
        => Assert.True(new VpnConfig().AppendOtpToPassword);

    [Fact]
    public void Deserialize_ConfigWrittenBeforeTheOtpSetting_GetsAppendedMode()
    {
        // A file saved by an older build has no key for it at all.
        var c = JsonSerializer.Deserialize<VpnConfig>("""{ "Username": "u", "Password": "pw" }""")!;
        Assert.True(c.AppendOtpToPassword);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtpMode_SurvivesSanitizeAndSerialization(bool append)
    {
        var c = FullyConfigured();
        c.AppendOtpToPassword = append;

        Assert.Equal(append, c.Sanitized().AppendOtpToPassword);
        var roundTripped = JsonSerializer.Deserialize<VpnConfig>(JsonSerializer.Serialize(c))!;
        Assert.Equal(append, roundTripped.AppendOtpToPassword);
    }

    [Fact]
    public void TunnelSettingsMatch_IsFalseWhenTheOtpModeDiffers()
    {
        var before = FullyConfigured();
        var after = FullyConfigured();
        after.AppendOtpToPassword = !before.AppendOtpToPassword;

        Assert.False(before.TunnelSettingsMatch(after));
    }
}
