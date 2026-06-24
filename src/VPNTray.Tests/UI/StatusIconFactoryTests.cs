using VPNTray.Core;
using VPNTray.UI;

namespace VPNTray.Tests.UI;

public class StatusIconFactoryTests
{
    [Theory]
    [InlineData(VpnStatus.Connected, "green")]
    [InlineData(VpnStatus.Connecting, "orange")]
    [InlineData(VpnStatus.Failed, "red")]
    [InlineData(VpnStatus.Disconnected, "gray")]
    public void DotColorName_MapsEachStatus(VpnStatus status, string expected)
        => Assert.Equal(expected, StatusIconFactory.DotColorName(status));

    [Fact]
    public void IconResourcePath_ReturnsStatusSpecificFile()
    {
        Assert.Contains("connected", StatusIconFactory.IconResourcePath(VpnStatus.Connected));
        Assert.Contains("disconnected", StatusIconFactory.IconResourcePath(VpnStatus.Disconnected));
    }
}
