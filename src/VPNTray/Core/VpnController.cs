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
    private CancellationTokenSource? _loginConnect;
    private int _shutdownStarted;

    /// <summary>
    /// How long the logon connect waits for the network before standing down. Generous on
    /// purpose: it costs nothing while waiting (event-driven, no dial), and slow Wi-Fi plus
    /// 802.1x plus DHCP can genuinely take this long after a cold boot.
    /// </summary>
    private static readonly TimeSpan DefaultLoginNetworkWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Pauses between logon connect attempts. Needed because "network available" only means an
    /// adapter came up — the route to the gateway, DNS, or a captive portal can lag behind it.
    /// </summary>
    private static readonly TimeSpan[] DefaultLoginRetryBackoff =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
    };

    private readonly TimeSpan _loginNetworkWait;
    private readonly IReadOnlyList<TimeSpan> _loginRetryBackoff;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, Task<IReadOnlyList<string>>> _resolveGateway;

    /// <summary>
    /// TOTP window of the last code this app appended to a password, or null if none yet.
    /// Only touched under <see cref="_gate"/>.
    /// </summary>
    private long? _lastOtpCounter;

    /// <summary>Set while a connect sits out the rest of an OTP window; Disconnect cancels it.</summary>
    private CancellationTokenSource? _otpWait;

    /// <param name="loginNetworkWait">Test seam — production callers take the default.</param>
    /// <param name="loginRetryBackoff">Test seam — production callers take the default.</param>
    /// <param name="clock">Test seam for the OTP window — production callers take the default.</param>
    /// <param name="delay">Test seam for the OTP-window wait — production callers take the default.</param>
    /// <param name="resolveGateway">Test seam for the gateway-node lookup — production callers take the default.</param>
    public VpnController(
        IOpenConnectProcessRunning openConnect,
        INetworkMonitor network,
        Func<VpnConfig?> loadConfig,
        ISplitDnsConfigurator splitDns,
        TimeSpan? loginNetworkWait = null,
        IReadOnlyList<TimeSpan>? loginRetryBackoff = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, Task<IReadOnlyList<string>>>? resolveGateway = null)
    {
        _resolveGateway = resolveGateway ?? GatewayAddress.ResolveAllAsync;
        _openConnect = openConnect;
        _network = network;
        _loadConfig = loadConfig;
        _splitDns = splitDns;
        _loginNetworkWait = loginNetworkWait ?? DefaultLoginNetworkWait;
        _loginRetryBackoff = loginRetryBackoff ?? DefaultLoginRetryBackoff;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
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

            long? otpCounterBefore = _lastOtpCounter;
            string password;
            try { password = await LoginPasswordAsync(config); }
            catch (OperationCanceledException)
            {
                // Disconnect or shutdown cancelled the OTP wait; Disconnect sets the state
                // itself once it gets the gate.
                return;
            }

            try
            {
                var handshake = await DialAsync(config, password, pinnedAddress: null);
                if (handshake.Reason == HandshakeAnalyzer.GatewayUnreachableReason)
                    handshake = await DialOtherGatewayNodesAsync(config, otpCounterBefore, handshake);

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
                    if (handshake.Reason == HandshakeAnalyzer.AuthFailedReason)
                    {
                        // A rejected login is not something a retry fixes, and the gateway
                        // locks the account after a few in a row. Stop every automatic redial
                        // (logon retries, network-return reconnects) until the user acts.
                        ShouldAutoReconnect = false;
                        AppLogger.Shared.Info("authentication rejected, automatic reconnects disabled");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A disconnect cancelled the OTP wait between two gateway nodes.
                _openConnect.Stop();
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

    private async Task<OpenConnectHandshake> DialAsync(VpnConfig config, string password, string? pinnedAddress)
    {
        _openConnect.Start(config, password, pinnedAddress);
        return await _openConnect.WaitForHandshakeAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// After the gateway dropped the connection before login, tries each of its addresses
    /// in turn, pinned with --resolve, and returns the first outcome that is not another such
    /// drop — or the last drop if every node failed the same way.
    ///
    /// Covers a gateway name served by several nodes where one accepts TCP but kills the TLS
    /// handshake: openconnect itself never moves on to another address. Every node is tried,
    /// the one that just failed included, because nothing reports which one that was.
    ///
    /// A drop happens before any credentials are sent, so the OTP of the failed dial was never
    /// spent: the window bookkeeping is rolled back to <paramref name="otpCounterBefore"/>
    /// before each retry, letting it reuse the same code instead of sitting out the window.
    /// </summary>
    private async Task<OpenConnectHandshake> DialOtherGatewayNodesAsync(
        VpnConfig config, long? otpCounterBefore, OpenConnectHandshake failed)
    {
        string? host = GatewayAddress.HostName(config.Gateway);
        if (host is null) return failed;

        IReadOnlyList<string> nodes;
        try { nodes = await _resolveGateway(host); }
        catch (Exception ex)
        {
            AppLogger.Shared.Error("gateway fallback: lookup failed: " + ex.Message);
            return failed;
        }
        // One address is the one openconnect just failed on; there is nowhere else to go.
        if (nodes.Count < 2) return failed;

        var last = failed;
        foreach (var node in nodes.Take(GatewayAddress.MaxFallbackAddresses))
        {
            AppLogger.Shared.Info($"gateway dropped the connection before login, retrying via {node}");
            _lastOtpCounter = otpCounterBefore;
            string password = await LoginPasswordAsync(config);
            last = await DialAsync(config, password, node);
            if (last.Reason != HandshakeAnalyzer.GatewayUnreachableReason) return last;
        }
        return last;
    }

    /// <summary>
    /// What goes on openconnect's stdin. In separate-prompt mode only the password —
    /// openconnect answers the OTP challenge itself via --token-mode=totp. In appended mode the
    /// password followed directly by the current code, after waiting out the window if this
    /// app already spent its code or it is about to expire (see <see cref="OtpWindow"/>).
    /// Caller holds <see cref="_gate"/>. Throws <see cref="OperationCanceledException"/> when
    /// a disconnect cancels the wait.
    /// </summary>
    private async Task<string> LoginPasswordAsync(VpnConfig config)
    {
        if (!config.AppendOtpToPassword) return config.Password;

        var wait = OtpWindow.WaitBeforeSend(_clock(), _lastOtpCounter);
        if (wait > TimeSpan.Zero)
        {
            AppLogger.Shared.Info($"OTP: waiting {wait.TotalSeconds:0.0}s for a fresh code");
            var cts = new CancellationTokenSource();
            _otpWait = cts;
            try { await _delay(wait, cts.Token); }
            finally { _otpWait = null; } // not disposed: Cancel may arrive from another thread
        }

        var now = _clock();
        // Recorded before sending: once the code is on the wire the gateway counts it as
        // used, whatever the outcome of the handshake.
        _lastOtpCounter = OtpWindow.Counter(now);
        // Do NOT log the result.
        return config.Password + TotpGenerator.Code(config.TotpSecret, now);
    }

    /// <summary>
    /// The logon-start connect. A plain <see cref="ConnectAsync"/> assumes the network is
    /// there, which right after logon it routinely is not — Wi-Fi association, 802.1x and DHCP
    /// all trail the desktop by seconds, and a dial into that gap burns its whole handshake
    /// timeout and fails. Three differences from a manual connect, all from that one fact:
    ///
    /// 1. Waits (bounded, event-driven) for the network before the first dial.
    /// 2. Arms <see cref="ShouldAutoReconnect"/> up front rather than on success: connect-at-login
    ///    is the user saying "be connected", and a slow network should postpone that, not veto
    ///    it. The reachability handler finishes the job when the network eventually shows up.
    /// 3. Retries with backoff, because "network available" only means an adapter came up —
    ///    the route to the gateway can lag behind it.
    /// </summary>
    public async Task ConnectAtLoginAsync()
    {
        ShouldAutoReconnect = true;

        var cts = new CancellationTokenSource();
        _loginConnect = cts;
        try
        {
            if (!await WaitForNetworkAsync(_loginNetworkWait, cts.Token))
            {
                // Not Failed — nothing was attempted. The armed intent connects on the next
                // reachability event instead.
                AppLogger.Shared.Info(
                    $"login connect: no network after {_loginNetworkWait.TotalSeconds:0}s, " +
                    "standing by until it returns");
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                // A Disconnect during the backoff withdraws the intent — honour it.
                if (!ShouldAutoReconnect) return;

                await ConnectAsync();
                if (State.Status == VpnStatus.Connected) return;
                // Cleared by an auth rejection: retrying a wrong password only locks the account.
                if (!ShouldAutoReconnect) return;

                if (attempt >= _loginRetryBackoff.Count)
                {
                    AppLogger.Shared.Info(
                        "login connect: attempts exhausted, waiting for a network change");
                    return;
                }
                await Task.Delay(_loginRetryBackoff[attempt], cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect or shutdown pulled the plug; both already put the state where it belongs.
        }
        finally
        {
            // Cleared but never disposed: Cancel arrives from other threads (Disconnect,
            // shutdown) and must not be able to race a Dispose.
            _loginConnect = null;
        }
    }

    /// <summary>
    /// True the moment the network is up, false when <paramref name="limit"/> runs out first.
    /// Event-driven rather than polled; the double IsAvailable check closes the gap where the
    /// network comes up between the first look and the subscription, whose event nobody heard.
    /// </summary>
    private async Task<bool> WaitForNetworkAsync(TimeSpan limit, CancellationToken token)
    {
        if (_network.IsAvailable) return true;

        var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUp(bool reachable) { if (reachable) up.TrySetResult(true); }
        _network.ReachabilityChanged += OnUp;
        try
        {
            if (_network.IsAvailable) return true;
            AppLogger.Shared.Info(
                $"login connect: network not up yet, waiting up to {limit.TotalSeconds:0}s");
            var winner = await Task.WhenAny(up.Task, Task.Delay(limit, token));
            token.ThrowIfCancellationRequested();
            return winner == up.Task;
        }
        finally
        {
            _network.ReachabilityChanged -= OnUp;
        }
    }

    public async Task DisconnectAsync()
    {
        // A login connect still waiting on the network or sitting out a backoff must not
        // redial after an explicit disconnect.
        _loginConnect?.Cancel();
        // Likewise a connect sitting out an OTP window, which holds the gate while it waits.
        _otpWait?.Cancel();
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
        try { _loginConnect?.Cancel(); } catch { /* ignore */ }
        try { _otpWait?.Cancel(); } catch { /* ignore */ }

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
        // Failed counts alongside Disconnected: a connect the network's absence defeated — the
        // logon dial, or a recovery attempt that lost the race with DHCP — lands in Failed, and
        // with the intent still armed the next network arrival is exactly the cue to try again.
        // Manual connects stay out of this: they never arm the intent on failure.
        else if (reachable && ShouldAutoReconnect &&
                 State.Status is VpnStatus.Disconnected or VpnStatus.Failed)
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
