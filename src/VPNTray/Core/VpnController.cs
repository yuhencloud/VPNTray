using System.ComponentModel;
using System.Runtime.CompilerServices;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.Core;

/// <remarks>
/// Threading: <see cref="PropertyChanged"/> may be raised on a thread-pool thread
/// (watchdog / reconnect continuations), not just the caller's thread. UI consumers
/// must marshal observation of <see cref="State"/> to the UI thread — the app's
/// AppCoordinator does this via Dispatcher.Invoke. This Core class intentionally has
/// no WPF/Dispatcher dependency.
/// Connect/disconnect transitions are serialized via an internal gate so overlapping
/// user actions and network-change events cannot double-spawn openconnect.
/// </remarks>
public sealed class VpnController : INotifyPropertyChanged
{
    private readonly IOpenConnectProcessRunning _openConnect;
    private readonly INetworkMonitor _network;
    private readonly Func<VpnConfig?> _loadConfig;
    private readonly ISplitDnsConfigurator _splitDns;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VpnState _state = VpnState.Disconnected;
    private CancellationTokenSource? _watchdog;
    private int _shutdownStarted;

    public VpnController(
        IOpenConnectProcessRunning openConnect,
        INetworkMonitor network,
        Func<VpnConfig?> loadConfig,
        ISplitDnsConfigurator splitDns)
    {
        _openConnect = openConnect;
        _network = network;
        _loadConfig = loadConfig;
        _splitDns = splitDns;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VpnState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldAutoReconnect { get; private set; }

    public void StartNetworkMonitoring()
    {
        _network.ReachabilityChanged += OnReachabilityChanged;
        _network.Start();
    }

    public async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Idempotency guard: a concurrent/duplicate connect must not double-spawn.
            if (State.Status == VpnStatus.Connecting || State.Status == VpnStatus.Connected)
            {
                return;
            }

            var config = (_loadConfig() ?? new VpnConfig()).Sanitized();
            if (!config.IsConfigured)
            {
                State = VpnState.Failed("Authentication required — configuration is incomplete. Open Settings.");
                return;
            }

            // Fail fast on a malformed secret rather than letting openconnect reject it
            // mid-handshake, where the reason is much harder to read back.
            try { TotpGenerator.Base32Decode(config.TotpSecret); }
            catch (TotpException ex)
            {
                State = VpnState.Failed("Invalid TOTP secret: " + ex.Message);
                return;
            }

            State = VpnState.Connecting;
            try
            {
                // Only the password goes over stdin. openconnect answers the second,
                // separate OTP/challenge prompt itself via --token-mode=totp.
                _openConnect.Start(config, config.Password);
                var handshake = await _openConnect.WaitForHandshakeAsync(TimeSpan.FromSeconds(20));
                if (handshake.Connected)
                {
                    // Before announcing Connected, so the tunnel is never briefly advertised
                    // as usable while public names still resolve to unroutable addresses.
                    await ApplySplitDnsAsync(config);
                    State = VpnState.Connected(DateTimeOffset.Now);
                    ShouldAutoReconnect = true;
                    StartWatchdog();
                }
                else
                {
                    _openConnect.Stop();
                    State = VpnState.Failed(handshake.Reason ?? "Connection failed");
                }
            }
            catch (Exception ex)
            {
                _openConnect.Stop();
                AppLogger.Shared.Error("connect error: " + ex.Message);
                State = VpnState.Failed("Connection error: " + ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ShouldAutoReconnect = false;
            await StopAndClearAsync();
            State = VpnState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Network-loss disconnect — preserves ShouldAutoReconnect so recovery can retry.</summary>
    private async Task AutoDisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopAndClearAsync();
            State = VpnState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAndClearAsync()
    {
        _watchdog?.Cancel();
        _watchdog?.Dispose();
        _watchdog = null;
        try { _openConnect.Stop(); } catch { /* ignore */ }
        // Unconditional, not gated on the DNS mode: every rule either mode installs names a
        // resolver that goes away with the adapter, so leaving them behind pins those domains
        // to a dead server — including across a settings change that emptied the lists.
        // Off the caller's thread because the WPF sync context resumes the connect/disconnect
        // continuations on the UI thread, and this spawns powershell.
        await Task.Run(_splitDns.Cleanup);
    }

    /// <summary>
    /// Drops split-DNS rules left behind by a run that never got to clean up — terminated by the
    /// installer, ended from Task Manager, or lost to a crash. NRPT rules are machine-wide and
    /// outlive the process, so until something removes them the affected domains stay pinned to
    /// the public resolvers with no tunnel to justify it.
    ///
    /// Connect clears leftovers too, but only once the user next connects; this covers the gap
    /// from app start until then. No-op while connected, where the rules are not stale at all.
    /// </summary>
    public void RemoveStaleSplitDnsRules()
    {
        if (State.Status == VpnStatus.Connected) return;
        _splitDns.Cleanup();
    }

    /// <summary>
    /// Re-reads the config and swaps the split-DNS rules underneath a live tunnel. For a
    /// settings change that only moved the suffix lists: the rules are NRPT entries, which
    /// openconnect neither installs nor knows about, so there is nothing to reconnect for.
    ///
    /// No-op unless connected — Connect installs them itself, and applying them without a
    /// tunnel would point real traffic at the exception resolvers for no reason.
    /// </summary>
    public async Task RefreshSplitDnsAsync()
    {
        if (State.Status != VpnStatus.Connected) return;

        var config = _loadConfig()?.Sanitized();
        if (config is null) return;

        // Says so out loud because the alternative — a full reconnect — is otherwise only
        // distinguishable in the log by the absence of lines, which is a poor way to verify
        // that the tunnel really was left alone.
        AppLogger.Shared.Info("split-DNS: refreshing rules, tunnel untouched");

        await ApplySplitDnsAsync(config);
    }

    /// <summary>
    /// Installs the rules the current mode calls for — and removes this app's rules when it
    /// calls for none, rather than returning early. "Nothing to install" and "nothing should be
    /// installed" are the same state, and only one of them is reached by doing nothing: an
    /// earlier Cleanup that failed, or a list emptied under a live connection, both leave rules
    /// behind that would otherwise survive the whole next session.
    /// </summary>
    private async Task ApplySplitDnsAsync(VpnConfig config)
    {
        var plan = await BuildSplitDnsPlanAsync(config);
        if (plan is null) await Task.Run(_splitDns.Cleanup);
        else await Task.Run(() => _splitDns.Apply(plan.Value.Namespaces, plan.Value.Servers));
    }

    private readonly record struct SplitDnsRules(
        IReadOnlyList<string> Namespaces, IReadOnlyList<string> Servers);

    /// <summary>
    /// Which suffixes get an NRPT rule, and which resolvers those rules point at. Null when
    /// the mode in effect installs none.
    ///
    /// The two DNS modes are mirror images of each other. Internal-DNS mode hands the whole
    /// system to the tunnel's resolvers and redirects a list of public suffixes back out to
    /// configured public ones; public-DNS mode leaves system DNS alone and redirects a list of
    /// internal suffixes the other way, into the tunnel. Only the second one has no server list
    /// to read: its resolvers are the ones the gateway pushed for this session, which only
    /// openconnect ever learns.
    /// </summary>
    private async Task<SplitDnsRules?> BuildSplitDnsPlanAsync(VpnConfig config)
    {
        if (!config.SkipDnsModification)
            return new SplitDnsRules(config.SplitDnsPublicNamespaces, config.SplitDnsPublicServers);

        // Checked before asking for the resolvers, so a config with no internal suffixes never
        // pays the wait for a report it has no use for.
        if (config.SplitDnsInternalNamespaces.Count == 0) return null;

        var pushed = await _openConnect.TunnelDnsServersAsync();
        if (pushed.Count == 0)
        {
            AppLogger.Shared.Error(
                "split-DNS: no usable resolvers were reported for this session, so the " +
                "internal-DNS domains install no rules and keep resolving through public DNS");
            return null;
        }
        return new SplitDnsRules(config.SplitDnsInternalNamespaces, pushed);
    }

    /// <summary>
    /// Best-effort SYNCHRONOUS stop for app shutdown: drops the network subscription, kills
    /// openconnect and cancels the watchdog WITHOUT the async gate, state changes, or
    /// PropertyChanged. Idempotent — later calls return immediately. Use only on the exit
    /// path where the controller will not be used again.
    ///
    /// Each step is logged: this runs once, and when the app failed to exit the log was the
    /// only thing that could say which step never returned.
    /// </summary>
    public void StopForShutdown()
    {
        // Quit, App.OnExit and SessionEnding all funnel here. Without this guard each path
        // spawns its own powershell and pays the wait below again, doubling exit latency.
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;

        AppLogger.Shared.Info("shutdown: stopping network monitor");
        // First, so nothing can queue more work while the rest is torn down. NetworkChange is
        // a process-wide static event; a subscription left behind outlives this controller.
        try { _network.ReachabilityChanged -= OnReachabilityChanged; _network.Stop(); } catch { /* ignore */ }

        AppLogger.Shared.Info("shutdown: cancelling watchdog");
        try { _watchdog?.Cancel(); _watchdog?.Dispose(); _watchdog = null; } catch { /* ignore */ }

        AppLogger.Shared.Info("shutdown: stopping openconnect");
        try { _openConnect.Stop(); } catch { /* ignore */ }

        AppLogger.Shared.Info("shutdown: removing split-DNS rules");
        // Bounded wait: quitting must not hang on a slow powershell. If the wait expires the
        // child is left running and still removes the rules — it is not killed here.
        try { Task.Run(_splitDns.Cleanup).Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }

        AppLogger.Shared.Info("shutdown: controller stopped");
    }

    public void MarkSetupIncomplete()
        => State = VpnState.Failed(VpnState.SetupIncompletePrefix + " — finish onboarding to connect.");

    /// <summary>
    /// Clear the setup-incomplete lock-down once configuration is complete (e.g. after
    /// the onboarding wizard finishes), so the tray exposes Connect again. No-op if the
    /// controller is not currently in the setup-incomplete state (don't clobber a live
    /// connection/failure).
    /// </summary>
    public void MarkConfigured()
    {
        if (State.IsSetupIncomplete) State = VpnState.Disconnected;
    }

    private void OnReachabilityChanged(bool reachable)
    {
        if (!reachable && State.Status == VpnStatus.Connected)
        {
            _ = AutoDisconnectAsync();
        }
        else if (reachable && State.Status == VpnStatus.Disconnected && ShouldAutoReconnect)
        {
            _ = ConnectAsync();
        }
    }

    private void StartWatchdog()
    {
        _watchdog?.Cancel();
        _watchdog?.Dispose();
        _watchdog = new CancellationTokenSource();
        var token = _watchdog.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token).ContinueWith(_ => { });
                if (token.IsCancellationRequested) return;
                if (!_openConnect.IsRunning() && State.Status == VpnStatus.Connected)
                {
                    State = VpnState.Disconnected;
                    AppLogger.Shared.Info("watchdog: openconnect vanished, marking disconnected");
                    // openconnect died without going through Disconnect, so nothing else will
                    // take the rules down. Already on a pool thread, so call it directly.
                    try { _splitDns.Cleanup(); } catch { /* ignore */ }
                    return;
                }
            }
        }, token);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
