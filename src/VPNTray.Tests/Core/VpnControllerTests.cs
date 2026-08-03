using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Tests.Core;

internal sealed class FakeOpenConnect : IOpenConnectProcessRunning
{
    public OpenConnectHandshake NextHandshake = new(true, null);
    public bool Running;
    public int StartCount;
    public int StopCount;
    public string? LastPassword;

    public void Start(VpnConfig config, string password) { StartCount++; LastPassword = password; Running = true; }
    public Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout)
    {
        Running = NextHandshake.Connected;
        return Task.FromResult(NextHandshake);
    }
    public bool IsRunning() => Running;
    public void Stop() { StopCount++; Running = false; }
}

public class VpnControllerTests
{
    private static VpnConfig Good() => new()
    {
        Username = "u", Password = "pw", TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "g", ServerCertPin = "pin-sha256:AAAA",
    };

    [Fact]
    public async Task Connect_Success_SetsConnectedAndAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var net = new FakeNetworkMonitor();
        var c = new VpnController(oc, net, () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Connected, c.State.Status);
        Assert.True(c.ShouldAutoReconnect);
        // Stage 1 only: no TOTP is appended — openconnect answers the OTP prompt itself.
        Assert.Equal("pw", oc.LastPassword);
    }

    [Fact]
    public async Task Connect_WhenTotpSecretIsMalformed_FailsWithoutSpawning()
    {
        var oc = new FakeOpenConnect();
        var cfg = Good();
        cfg.TotpSecret = "not-base32!";
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => cfg);

        await c.ConnectAsync();

        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.Contains("TOTP secret", c.State.Reason);
        Assert.Equal(0, oc.StartCount);
    }

    [Fact]
    public async Task Connect_Failure_SetsFailed_AndDoesNotArmAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "Authentication failed") };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);
    }

    [Fact]
    public async Task Connect_WhenNotConfigured_FailsWithoutSpawning()
    {
        var oc = new FakeOpenConnect();
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => new VpnConfig());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.Equal(0, oc.StartCount);
    }

    [Fact]
    public async Task Disconnect_ClearsAutoReconnect_AndStops()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        await c.DisconnectAsync();
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);
        Assert.True(oc.StopCount >= 1);
    }

    [Fact]
    public async Task NetworkLost_WhileConnected_AutoDisconnects_ButKeepsIntent()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var net = new FakeNetworkMonitor();
        var c = new VpnController(oc, net, () => Good());
        c.StartNetworkMonitoring();
        await c.ConnectAsync();
        net.Raise(false);
        await Task.Delay(50);
        Assert.NotEqual(VpnStatus.Connected, c.State.Status);
        Assert.True(c.ShouldAutoReconnect); // intent preserved for recovery
    }

    [Fact]
    public async Task Connect_WhenAlreadyConnected_DoesNotSpawnAgain()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Connected, c.State.Status);
        Assert.Equal(1, oc.StartCount);

        await c.ConnectAsync(); // idempotent no-op: already Connected
        Assert.Equal(1, oc.StartCount);
    }

    [Fact]
    public async Task StopForShutdown_StopsOpenConnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        c.StopForShutdown();
        Assert.True(oc.StopCount >= 1);
    }

    [Fact]
    public void MarkSetupIncomplete_SetsSetupIncompleteState()
    {
        var c = new VpnController(new FakeOpenConnect(), new FakeNetworkMonitor(), () => new VpnConfig());
        c.MarkSetupIncomplete();
        Assert.True(c.State.IsSetupIncomplete);
    }

    [Fact]
    public void MarkConfigured_ClearsSetupIncomplete_ToDisconnected()
    {
        var c = new VpnController(new FakeOpenConnect(), new FakeNetworkMonitor(), () => new VpnConfig());
        c.MarkSetupIncomplete();
        Assert.True(c.State.IsSetupIncomplete);
        c.MarkConfigured();
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);
        Assert.False(c.State.IsSetupIncomplete);
    }
}
