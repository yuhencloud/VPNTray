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

internal sealed class FakeSplitDns : ISplitDnsConfigurator
{
    public int ApplyCount;
    public int CleanupCount;
    public IReadOnlyList<string>? LastNamespaces;
    public IReadOnlyList<string>? LastServers;

    public void Apply(IReadOnlyList<string> namespaces, IReadOnlyList<string> servers)
    {
        ApplyCount++;
        LastNamespaces = namespaces;
        LastServers = servers;
    }

    public void Cleanup() => CleanupCount++;
}

public class VpnControllerTests
{
    private static VpnConfig Good() => new()
    {
        Username = "u", Password = "pw", TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "g", ServerCertPin = "pin-sha256:AAAA",
    };

    private static VpnController Build(
        IOpenConnectProcessRunning openConnect,
        INetworkMonitor network,
        Func<VpnConfig?> loadConfig,
        ISplitDnsConfigurator? splitDns = null)
        => new(openConnect, network, loadConfig, splitDns ?? new FakeSplitDns());

    [Fact]
    public async Task Connect_Success_SetsConnectedAndAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var net = new FakeNetworkMonitor();
        var c = Build(oc, net, () => Good());
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
        var c = Build(oc, new FakeNetworkMonitor(), () => cfg);

        await c.ConnectAsync();

        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.Contains("TOTP secret", c.State.Reason);
        Assert.Equal(0, oc.StartCount);
    }

    [Fact]
    public async Task Connect_Failure_SetsFailed_AndDoesNotArmAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "Authentication failed") };
        var c = Build(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);
    }

    [Fact]
    public async Task Connect_WhenNotConfigured_FailsWithoutSpawning()
    {
        var oc = new FakeOpenConnect();
        var c = Build(oc, new FakeNetworkMonitor(), () => new VpnConfig());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.Equal(0, oc.StartCount);
    }

    [Fact]
    public async Task Disconnect_ClearsAutoReconnect_AndStops()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var c = Build(oc, new FakeNetworkMonitor(), () => Good());
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
        var c = Build(oc, net, () => Good());
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
        var c = Build(oc, new FakeNetworkMonitor(), () => Good());
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
        var c = Build(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        c.StopForShutdown();
        Assert.True(oc.StopCount >= 1);
    }

    [Fact]
    public async Task Connect_Success_InstallsTheSplitDnsRules()
    {
        var dns = new FakeSplitDns();
        var cfg = Good();
        cfg.SplitDnsPublicNamespaces = new List<string> { ".example.com" };
        cfg.SplitDnsPublicServers = new List<string> { "8.8.8.8" };
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(1, dns.ApplyCount);
        Assert.Equal(new[] { ".example.com" }, dns.LastNamespaces);
        Assert.Equal(new[] { "8.8.8.8" }, dns.LastServers);
    }

    [Fact]
    public async Task Connect_WhenTunnelDnsIsDisabled_InstallsNothing()
    {
        // Nothing points at the internal resolver, so there is nothing to carve back out.
        var dns = new FakeSplitDns();
        var cfg = Good();
        cfg.SkipDnsModification = true;
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(0, dns.ApplyCount);
    }

    [Fact]
    public async Task Connect_Failure_InstallsNothing()
    {
        var dns = new FakeSplitDns();
        var oc = new FakeOpenConnect { NextHandshake = new(false, "Authentication failed") };
        var c = Build(oc, new FakeNetworkMonitor(), () => Good(), dns);

        await c.ConnectAsync();

        Assert.Equal(0, dns.ApplyCount);
    }

    [Fact]
    public async Task Disconnect_RemovesTheSplitDnsRules()
    {
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);
        await c.ConnectAsync();

        await c.DisconnectAsync();

        Assert.True(dns.CleanupCount >= 1);
    }

    [Fact]
    public async Task NetworkLost_RemovesTheSplitDnsRules()
    {
        // The rules outlive the adapter otherwise, pinning those domains to public DNS
        // even after the tunnel is gone.
        var dns = new FakeSplitDns();
        var net = new FakeNetworkMonitor();
        var c = Build(new FakeOpenConnect(), net, () => Good(), dns);
        c.StartNetworkMonitoring();
        await c.ConnectAsync();

        net.Raise(false);
        await Task.Delay(100);

        Assert.True(dns.CleanupCount >= 1);
    }

    [Fact]
    public async Task StopForShutdown_RemovesTheSplitDnsRules()
    {
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);
        await c.ConnectAsync();

        c.StopForShutdown();

        Assert.True(dns.CleanupCount >= 1);
    }

    /// <summary>
    /// Quit, App.OnExit and SessionEnding all funnel into this, so a non-idempotent
    /// implementation spawns powershell — and pays its bounded wait — once per path.
    /// </summary>
    [Fact]
    public async Task StopForShutdown_CalledRepeatedly_CleansUpExactlyOnce()
    {
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);
        await c.ConnectAsync();

        c.StopForShutdown();
        c.StopForShutdown();
        c.StopForShutdown();

        Assert.Equal(1, dns.CleanupCount);
    }

    /// <summary>
    /// NetworkChange is a process-wide static event, so a subscription left behind on the
    /// exit path keeps the controller reachable and can still fire during teardown.
    /// </summary>
    [Fact]
    public void StopForShutdown_StopsNetworkMonitoring()
    {
        var net = new FakeNetworkMonitor();
        var c = Build(new FakeOpenConnect(), net, () => Good());
        c.StartNetworkMonitoring();

        c.StopForShutdown();

        Assert.False(net.Started);
    }

    [Fact]
    public async Task RefreshSplitDns_WhenConnected_ReappliesRulesWithoutTouchingTheTunnel()
    {
        var dns = new FakeSplitDns();
        var oc = new FakeOpenConnect();
        var cfg = Good();
        var c = Build(oc, new FakeNetworkMonitor(), () => cfg, dns);
        await c.ConnectAsync();
        var stopsAfterConnect = oc.StopCount;
        cfg.SplitDnsPublicNamespaces = new List<string> { ".example.com" };

        await c.RefreshSplitDnsAsync();

        Assert.Equal(2, dns.ApplyCount);
        Assert.Equal(new[] { ".example.com" }, dns.LastNamespaces);
        Assert.Equal(stopsAfterConnect, oc.StopCount);
        Assert.Equal(VpnStatus.Connected, c.State.Status);
    }

    [Fact]
    public async Task RefreshSplitDns_WhenNotConnected_DoesNothing()
    {
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);

        await c.RefreshSplitDnsAsync();

        Assert.Equal(0, dns.ApplyCount);
        Assert.Equal(0, dns.CleanupCount);
    }

    /// <summary>
    /// Reaching this state means the mode was switched without a reconnect. Leaving the rules
    /// in place would pin those domains to public DNS with nothing left to justify it.
    /// </summary>
    [Fact]
    public async Task RefreshSplitDns_WhenTunnelDnsIsDisabled_RemovesTheRules()
    {
        var dns = new FakeSplitDns();
        var cfg = Good();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);
        await c.ConnectAsync();
        cfg.SkipDnsModification = true;

        await c.RefreshSplitDnsAsync();

        Assert.Equal(1, dns.CleanupCount);
    }

    [Fact]
    public void StopForShutdown_UnsubscribesFromReachability()
    {
        var net = new FakeNetworkMonitor();
        var c = Build(new FakeOpenConnect(), net, () => Good());
        c.StartNetworkMonitoring();
        Assert.True(net.HasSubscribers);

        c.StopForShutdown();

        Assert.False(net.HasSubscribers);
    }

    [Fact]
    public void MarkSetupIncomplete_SetsSetupIncompleteState()
    {
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => new VpnConfig());
        c.MarkSetupIncomplete();
        Assert.True(c.State.IsSetupIncomplete);
    }

    [Fact]
    public void MarkConfigured_ClearsSetupIncomplete_ToDisconnected()
    {
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => new VpnConfig());
        c.MarkSetupIncomplete();
        Assert.True(c.State.IsSetupIncomplete);
        c.MarkConfigured();
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);
        Assert.False(c.State.IsSetupIncomplete);
    }
}
