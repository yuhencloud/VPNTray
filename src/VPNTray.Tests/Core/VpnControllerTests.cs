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

    /// <summary>Stands in for what the vpnc-script reports; empty is the "none pushed" case.</summary>
    public IReadOnlyList<string> TunnelDnsServers { get; set; } = Array.Empty<string>();

    public int TunnelDnsQueries;

    public Task<IReadOnlyList<string>> TunnelDnsServersAsync()
    {
        TunnelDnsQueries++;
        return Task.FromResult(TunnelDnsServers);
    }

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
        ISplitDnsConfigurator? splitDns = null,
        TimeSpan? loginNetworkWait = null,
        IReadOnlyList<TimeSpan>? loginRetryBackoff = null)
        => new(openConnect, network, loadConfig, splitDns ?? new FakeSplitDns(),
            loginNetworkWait, loginRetryBackoff);

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
        cfg.SkipDnsModification = false;   // internal-DNS mode: the only mode that installs rules
        cfg.SplitDnsPublicNamespaces = new List<string> { ".example.com" };
        cfg.SplitDnsPublicServers = new List<string> { "8.8.8.8" };
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(1, dns.ApplyCount);
        Assert.Equal(new[] { ".example.com" }, dns.LastNamespaces);
        Assert.Equal(new[] { "8.8.8.8" }, dns.LastServers);
    }

    [Fact]
    public async Task Connect_WhenTunnelDnsIsDisabled_AndNoInternalDomains_InstallsNothing()
    {
        // Nothing points at the internal resolver and nothing asks to, so there is no rule
        // either mode could want.
        var dns = new FakeSplitDns();
        var cfg = Good();
        cfg.SkipDnsModification = true;
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(0, dns.ApplyCount);
    }

    /// <summary>
    /// The mirror of the internal-DNS case: in public-DNS mode the listed internal suffixes are
    /// the ones redirected, and they go to the resolvers the gateway pushed — nothing the user
    /// configured, because per-session resolvers are not knowable in advance.
    /// </summary>
    [Fact]
    public async Task Connect_InPublicDnsMode_PointsTheInternalDomainsAtTheGatewaysResolvers()
    {
        var dns = new FakeSplitDns();
        var oc = new FakeOpenConnect { TunnelDnsServers = new[] { "10.0.0.53", "10.0.0.54" } };
        var cfg = Good();
        cfg.SkipDnsModification = true;
        cfg.SplitDnsInternalNamespaces = new List<string> { ".corp.example.com" };
        var c = Build(oc, new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(1, dns.ApplyCount);
        Assert.Equal(new[] { ".corp.example.com" }, dns.LastNamespaces);
        Assert.Equal(new[] { "10.0.0.53", "10.0.0.54" }, dns.LastServers);
    }

    /// <summary>
    /// A rule with no resolver to name cannot be written, and writing a wrong one would send
    /// internal names somewhere arbitrary. Log and leave them on public DNS instead.
    /// </summary>
    [Fact]
    public async Task Connect_InPublicDnsMode_WhenNoResolversWereReported_InstallsNothing()
    {
        var dns = new FakeSplitDns();
        var cfg = Good();
        cfg.SkipDnsModification = true;
        cfg.SplitDnsInternalNamespaces = new List<string> { ".corp.example.com" };
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);

        await c.ConnectAsync();

        Assert.Equal(0, dns.ApplyCount);
        Assert.Equal(VpnStatus.Connected, c.State.Status);   // the tunnel itself is unaffected
    }

    /// <summary>
    /// Both lists are excluded from TunnelSettingsMatch, so adding an internal domain mid
    /// session refreshes rather than reconnects — and that only works if the resolvers were
    /// picked up at connect time, before there was any list to spend them on.
    /// </summary>
    [Fact]
    public async Task RefreshSplitDns_InPublicDnsMode_InstallsDomainsAddedWhileConnected()
    {
        var dns = new FakeSplitDns();
        var oc = new FakeOpenConnect { TunnelDnsServers = new[] { "10.0.0.53" } };
        var cfg = Good();
        cfg.SkipDnsModification = true;
        var c = Build(oc, new FakeNetworkMonitor(), () => cfg, dns);
        await c.ConnectAsync();
        Assert.Equal(0, dns.ApplyCount);

        cfg.SplitDnsInternalNamespaces = new List<string> { ".corp.example.com" };
        await c.RefreshSplitDnsAsync();

        Assert.Equal(1, dns.ApplyCount);
        Assert.Equal(new[] { ".corp.example.com" }, dns.LastNamespaces);
        Assert.Equal(new[] { "10.0.0.53" }, dns.LastServers);
        Assert.Equal(VpnStatus.Connected, c.State.Status);
    }

    [Fact]
    public async Task RefreshSplitDns_InPublicDnsMode_WhenTheDomainsAreCleared_RemovesTheRules()
    {
        var dns = new FakeSplitDns();
        var oc = new FakeOpenConnect { TunnelDnsServers = new[] { "10.0.0.53" } };
        var cfg = Good();
        cfg.SkipDnsModification = true;
        cfg.SplitDnsInternalNamespaces = new List<string> { ".corp.example.com" };
        var c = Build(oc, new FakeNetworkMonitor(), () => cfg, dns);
        await c.ConnectAsync();
        var cleanupsAfterConnect = dns.CleanupCount;

        cfg.SplitDnsInternalNamespaces = new List<string>();
        await c.RefreshSplitDnsAsync();

        Assert.Equal(cleanupsAfterConnect + 1, dns.CleanupCount);
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
        // Connect cleans up too — public-DNS mode with no internal domains installs no rules,
        // and "install none" is implemented as "remove whatever is there". Count the delta.
        var cleanupsAfterConnect = dns.CleanupCount;

        c.StopForShutdown();
        c.StopForShutdown();
        c.StopForShutdown();

        Assert.Equal(cleanupsAfterConnect + 1, dns.CleanupCount);
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
        cfg.SkipDnsModification = false;   // internal-DNS mode: the only mode that installs rules
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
        cfg.SkipDnsModification = false;   // connect in the mode that installs rules
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => cfg, dns);
        await c.ConnectAsync();
        var cleanupsAfterConnect = dns.CleanupCount;
        cfg.SkipDnsModification = true;

        await c.RefreshSplitDnsAsync();

        Assert.Equal(cleanupsAfterConnect + 1, dns.CleanupCount);
    }

    /// <summary>
    /// The rules are machine-wide and outlive the process, so a run that was killed rather than
    /// asked to quit — by the installer, from Task Manager, or by a crash — leaves them pinning
    /// those domains to the public resolvers with no tunnel to justify it.
    /// </summary>
    [Fact]
    public void RemoveStaleSplitDnsRules_WhenNotConnected_RemovesWhateverIsLeftOver()
    {
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);

        c.RemoveStaleSplitDnsRules();

        Assert.Equal(1, dns.CleanupCount);
    }

    [Fact]
    public async Task RemoveStaleSplitDnsRules_WhileConnected_LeavesThemAlone()
    {
        // Not stale — they belong to this tunnel and are doing their job.
        var dns = new FakeSplitDns();
        var c = Build(new FakeOpenConnect(), new FakeNetworkMonitor(), () => Good(), dns);
        await c.ConnectAsync();
        var before = dns.CleanupCount;

        c.RemoveStaleSplitDnsRules();

        Assert.Equal(before, dns.CleanupCount);
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
    public async Task ConnectAtLogin_WhenNetworkIsUp_Connects()
    {
        var oc = new FakeOpenConnect();
        var c = Build(oc, new FakeNetworkMonitor(), () => Good());

        await c.ConnectAtLoginAsync();

        Assert.Equal(VpnStatus.Connected, c.State.Status);
        Assert.True(c.ShouldAutoReconnect);
    }

    [Fact]
    public async Task ConnectAtLogin_WaitsForTheNetwork_BeforeDialling()
    {
        var oc = new FakeOpenConnect();
        var net = new FakeNetworkMonitor { IsAvailable = false };
        var c = Build(oc, net, () => Good());

        var task = c.ConnectAtLoginAsync();
        await Task.Delay(100);
        Assert.Equal(0, oc.StartCount);   // no dial into a network that is not there yet

        net.IsAvailable = true;
        net.Raise(true);
        await task;

        Assert.Equal(VpnStatus.Connected, c.State.Status);
        Assert.Equal(1, oc.StartCount);
    }

    [Fact]
    public async Task ConnectAtLogin_WhenTheNetworkNeverComes_StandsDownButKeepsIntent()
    {
        var oc = new FakeOpenConnect();
        var net = new FakeNetworkMonitor { IsAvailable = false };
        var c = Build(oc, net, () => Good(), loginNetworkWait: TimeSpan.FromMilliseconds(50));

        await c.ConnectAtLoginAsync();

        Assert.Equal(0, oc.StartCount);
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);   // nothing was attempted, nothing failed
        Assert.True(c.ShouldAutoReconnect);   // the next reachability event finishes the job
    }

    [Fact]
    public async Task ConnectAtLogin_RetriesWithBackoff_ThenLeavesIntentArmed()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "gateway unreachable") };
        var c = Build(oc, new FakeNetworkMonitor(), () => Good(),
            loginRetryBackoff: new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) });

        await c.ConnectAtLoginAsync();

        Assert.Equal(3, oc.StartCount);   // the first attempt plus one per backoff entry
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.True(c.ShouldAutoReconnect);
    }

    /// <summary>
    /// The Failed-state seam: a login connect the network defeated lands in Failed, not
    /// Disconnected, and with the intent armed the reachability handler must retry from there.
    /// </summary>
    [Fact]
    public async Task ConnectAtLogin_FailedAttempt_IsRetriedWhenTheNetworkReturns()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "gateway unreachable") };
        var net = new FakeNetworkMonitor();
        var c = Build(oc, net, () => Good(), loginRetryBackoff: Array.Empty<TimeSpan>());
        c.StartNetworkMonitoring();

        await c.ConnectAtLoginAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);

        oc.NextHandshake = new(true, null);
        net.Raise(true);
        await WaitUntilAsync(() => c.State.Status == VpnStatus.Connected);

        Assert.Equal(VpnStatus.Connected, c.State.Status);
    }

    /// <summary>Polls instead of a fixed sleep: the reachability connect is fire-and-forget,
    /// and a fixed delay is either flaky on a loaded CI box or slow everywhere else.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Disconnect_WhileLoginConnectWaitsForNetwork_CancelsTheWait()
    {
        var oc = new FakeOpenConnect();
        var net = new FakeNetworkMonitor { IsAvailable = false };
        var c = Build(oc, net, () => Good(), loginNetworkWait: TimeSpan.FromMinutes(10));

        var login = c.ConnectAtLoginAsync();
        await c.DisconnectAsync();
        await login;   // must return promptly, not sit out the 10-minute wait

        Assert.Equal(0, oc.StartCount);
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);   // an explicit disconnect withdraws the intent
    }

    [Fact]
    public async Task Disconnect_WhileLoginConnectSitsOutItsBackoff_StopsTheRetries()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "gateway unreachable") };
        var c = Build(oc, new FakeNetworkMonitor(), () => Good(),
            loginRetryBackoff: new[] { TimeSpan.FromMinutes(10) });

        // The failure path runs synchronously against the fakes, so by the time the call
        // returns its task the first dial has failed and the backoff delay is pending.
        var login = c.ConnectAtLoginAsync();
        Assert.Equal(1, oc.StartCount);

        await c.DisconnectAsync();
        await login;   // must return promptly, not sit out the 10-minute backoff

        Assert.Equal(1, oc.StartCount);   // no further dial after the disconnect
        Assert.False(c.ShouldAutoReconnect);
    }

    [Fact]
    public async Task ManualConnectFailure_StillDoesNotAutoRetry()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "Authentication failed") };
        var net = new FakeNetworkMonitor();
        var c = Build(oc, net, () => Good());
        c.StartNetworkMonitoring();

        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);

        net.Raise(true);
        await Task.Delay(100);

        Assert.Equal(1, oc.StartCount);   // no intent, no retry
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
