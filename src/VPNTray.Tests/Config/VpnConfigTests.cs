using VPNTray.Config;

namespace VPNTray.Tests.Config;

public class VpnConfigTests
{
    private static VpnConfig FullyConfigured() => new()
    {
        Username = "first.last",
        PasswordPrefix = "Prefix2021",
        TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "vpn.example.com",
        ServerCertPin = "pin-sha256:AAAA",
    };

    [Fact]
    public void IsConfigured_TrueWhenAllFiveRequiredFieldsPresent()
        => Assert.True(FullyConfigured().IsConfigured);

    [Theory]
    [InlineData("Username")]
    [InlineData("PasswordPrefix")]
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
    public void Defaults_UseBundledPaths_AndSkipDnsOn()
    {
        var c = new VpnConfig();
        Assert.EndsWith("openconnect.exe", c.OpenconnectPath);
        Assert.True(c.SkipDnsModification);
    }
}
