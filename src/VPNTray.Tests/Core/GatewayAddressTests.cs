using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class GatewayAddressTests
{
    [Theory]
    [InlineData("vpn.example.com", "vpn.example.com")]
    [InlineData("vpn.example.com:8443", "vpn.example.com")]
    [InlineData("https://vpn.example.com/group", "vpn.example.com")]
    [InlineData("  ", null)]
    [InlineData("192.0.2.7", null)]        // already an address: nothing to pin
    [InlineData("[2001:db8::1]:443", null)]
    public void HostName_ExtractsTheNameToPin(string gateway, string? expected)
        => Assert.Equal(expected, GatewayAddress.HostName(gateway));

    [Theory]
    [InlineData("192.0.2.7", "vpn.example.com:192.0.2.7")]
    [InlineData("2001:db8::1", "vpn.example.com:2001:db8::1")]
    public void ResolveArgument_IsHostColonAddress(string address, string expected)
        => Assert.Equal(expected, GatewayAddress.ResolveArgument("vpn.example.com", address));

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("192.0.2.7 --script x")]
    public void ResolveArgument_RejectsAnythingButAnIpLiteral(string address)
        => Assert.Throws<ArgumentException>(() => GatewayAddress.ResolveArgument("vpn.example.com", address));
}
