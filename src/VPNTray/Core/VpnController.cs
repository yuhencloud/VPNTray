using System.ComponentModel;
using System.Runtime.CompilerServices;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.Core;

public sealed class VpnController : INotifyPropertyChanged
{
    private readonly IOpenConnectProcessRunning _openConnect;
    private readonly INetworkMonitor _network;
    private readonly Func<VpnConfig?> _loadConfig;

    private VpnState _state = VpnState.Disconnected;
    private CancellationTokenSource? _watchdog;

    public VpnController(IOpenConnectProcessRunning openConnect, INetworkMonitor network, Func<VpnConfig?> loadConfig)
    {
        _openConnect = openConnect;
        _network = network;
        _loadConfig = loadConfig;
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
        var config = (_loadConfig() ?? new VpnConfig()).Sanitized();
        if (!config.IsConfigured)
        {
            State = VpnState.Failed("Authentication required — configuration is incomplete. Open Settings.");
            return;
        }

        State = VpnState.Connecting;
        try
        {
            string code = TotpGenerator.Code(config.TotpSecret);
            string password = config.PasswordPrefix + code;
            _openConnect.Start(config, password);
            var handshake = await _openConnect.WaitForHandshakeAsync(TimeSpan.FromSeconds(5));
            if (handshake.Connected)
            {
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

    public async Task DisconnectAsync()
    {
        ShouldAutoReconnect = false;
        await StopAndClearAsync();
        State = VpnState.Disconnected;
    }

    /// <summary>Network-loss disconnect — preserves ShouldAutoReconnect so recovery can retry.</summary>
    private async Task AutoDisconnectAsync()
    {
        await StopAndClearAsync();
        State = VpnState.Disconnected;
    }

    private Task StopAndClearAsync()
    {
        _watchdog?.Cancel();
        _watchdog = null;
        try { _openConnect.Stop(); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    public void MarkSetupIncomplete()
        => State = VpnState.Failed(VpnState.SetupIncompletePrefix + " — finish onboarding to connect.");

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
                    return;
                }
            }
        }, token);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
