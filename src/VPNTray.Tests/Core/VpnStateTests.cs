using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class VpnStateTests
{
    [Fact]
    public void Disconnected_HasDisconnectedStatus()
        => Assert.Equal(VpnStatus.Disconnected, VpnState.Disconnected.Status);

    [Fact]
    public void Failed_WithSetupIncompletePrefix_IsSetupIncomplete()
        => Assert.True(VpnState.Failed("Setup incomplete — finish onboarding").IsSetupIncomplete);

    [Fact]
    public void Failed_WithOtherReason_IsNotSetupIncomplete()
        => Assert.False(VpnState.Failed("Authentication failed").IsSetupIncomplete);

    [Fact]
    public void Connected_CarriesSince()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(now, VpnState.Connected(now).Since);
    }
}
