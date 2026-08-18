using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;
using VPNTray.UI;
using VPNTray.Util;

namespace VPNTray;

public sealed class AppCoordinator
{
    private readonly ConfigStore _store = new();
    private readonly StartupManager _startup = new();
    private readonly VpnController _controller;
    private readonly DependencyChecker _checker = new(new SystemProcessRunner());
    private TaskbarIcon _tray = null!;
    private SettingsWindow? _settings;
    private SessionEndListener? _sessionEnd;
    private int _stopped;
    private static Timer? _exitBackstop;

    public AppCoordinator()
    {
        var openConnect = new OpenConnectProcess();
        var network = new NetworkMonitor();
        var splitDns = new SplitDnsManager(new SystemProcessRunner());
        _controller = new VpnController(openConnect, network, () => _store.Load(), splitDns);
        _controller.PropertyChanged += OnControllerPropertyChanged;
    }

    /// <summary>
    /// Posts rather than blocks: PropertyChanged arrives on thread-pool threads (watchdog,
    /// reconnect continuations), and the exit path deliberately blocks the UI thread while
    /// powershell removes the split-DNS rules. A blocking Invoke from that window parks the
    /// raising thread behind a UI thread that is itself waiting, and throws outright once the
    /// dispatcher has begun shutting down.
    /// </summary>
    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(UpdateTray); } catch { /* shutting down */ }
    }

    /// <summary>
    /// Windows asks the app to go away: a sign-out or shutdown, or Restart Manager clearing the
    /// way for an installer. Cleanup alone is not an answer — the process has to actually exit,
    /// or Restart Manager reports that it could not close the application and the install stops
    /// with "please ensure the applications holding files in use are closed".
    ///
    /// Logged because that failure is otherwise indistinguishable from Restart Manager never
    /// having reached the app at all, which is a different problem with a different fix.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        AppLogger.Shared.Info($"session ending ({e.ReasonSessionEnding}): shutting down");
        Exit();
    }

    /// <summary>
    /// Restart Manager (or a real shutdown) confirmed the session is ending. Reached through
    /// <see cref="SessionEndListener"/> rather than WPF's own event, which only fires when the
    /// app happens to have a window open.
    /// </summary>
    private void OnSessionEndConfirmed() => Exit();

    /// <summary>
    /// Tear down and go. Safe to reach twice: StopForShutdown is idempotent and a second
    /// Shutdown on an application already shutting down does nothing.
    /// </summary>
    private void Exit()
    {
        // Armed on every exit route, not only the tray menu. A teardown that wedges leaves
        // Restart Manager reporting it could not close the app, which is exactly what stops an
        // installer — so "eventually gone" has to be guaranteed, not hoped for.
        ArmForcedExit(TimeSpan.FromSeconds(15));
        StopForShutdown();
        AppLogger.Shared.Info("exit: calling Application.Shutdown");
        Application.Current.Shutdown();
        AppLogger.Shared.Info("exit: Application.Shutdown returned");
    }

    /// <param name="background">
    /// True only when the logon task started us. Every other route in — the installer's "Launch
    /// VPNTray", the Start menu, the exe — is a deliberate request, and answering it with nothing
    /// but a tray icon reads as the launch having failed.
    /// </param>
    public void Start(bool background = false)
    {
        _startup.ApplyPreference();

        // Before anything else, and kept for the life of the process: this is the only window the
        // app reliably has, and Restart Manager needs one to ask the app to close. Not disposed
        // on the exit path — it would mean destroying a window from inside its own message
        // handler, and the process is going away regardless.
        _sessionEnd = new SessionEndListener(OnSessionEndConfirmed);

        _tray = new TaskbarIcon { ToolTipText = "VPNTray" };
        _tray.TrayLeftMouseUp += (_, _) => ShowSettings();   // single left-click opens the main window
        BuildMenu();                                          // right-click shows the context menu
        _controller.StartNetworkMonitoring();
        Application.Current.SessionEnding += OnSessionEnding;

        // The installer terminates a running VPNTray to get at its files, and Task Manager and
        // crashes end it just as abruptly — none of which runs the exit path. Off the UI thread
        // because it shells out to powershell.
        var staleRulesRemoved = Task.Run(_controller.RemoveStaleSplitDnsRules);

        bool configured = _store.Load()?.IsConfigured == true;
        if (!configured) _controller.MarkSetupIncomplete();
        // Unconfigured always opens — there is nothing else the user could do next.
        if (!configured || !background) ShowSettings();
        UpdateTray();

        // Only the logon start, and only by opt-in: dialling a corporate VPN is not something
        // to do just because the app came up, and on a manual launch the user is right there.
        if (background && configured && _startup.IsConnectAtLoginEnabled())
        {
            AppLogger.Shared.Info("startup: connect-at-login is on, connecting");
            // Sequenced, not raced: the stale-rule sweep removes rules by tag, and at logon its
            // first powershell spawn can take as long as the handshake. Left unordered it can
            // land after the connect installs its rules and delete them again, which looks
            // exactly like the gateway having reported no resolvers.
            _ = staleRulesRemoved.ContinueWith(_ => _controller.ConnectAsync(), TaskScheduler.Default);
        }
    }

    private void BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        void Add(string header, RoutedEventHandler click, Func<bool>? visible = null)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += click;
            if (visible is not null) item.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed;
            menu.Items.Add(item);
        }

        Add("Connect", async (_, _) => await _controller.ConnectAsync());
        Add("Disconnect", async (_, _) => await _controller.DisconnectAsync());
        Add("Reconnect", async (_, _) => { await _controller.DisconnectAsync(); await _controller.ConnectAsync(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Open Settings…", (_, _) => ShowSettings());
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Quit", (_, _) => Quit());

        _tray.ContextMenu = menu;
    }

    private void UpdateTray()
    {
        if (_tray is null) return;

        var status = _controller.State.Status;
        _tray.IconSource = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(StatusIconFactory.IconResourcePath(status)));
        _tray.ToolTipText = $"VPNTray — {status}";

        // Setup-incomplete lock-down: hide connect/disconnect/reconnect.
        // Open Settings stays visible so the user can always reach configuration.
        bool incomplete = _controller.State.IsSetupIncomplete;
        var items = _tray.ContextMenu.Items;
        foreach (var obj in items)
        {
            if (obj is System.Windows.Controls.MenuItem mi)
            {
                var header = mi.Header?.ToString() ?? "";
                if (header is "Connect" or "Disconnect" or "Reconnect")
                    mi.Visibility = incomplete ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void ShowSettings()
    {
        // Single window only: if it is already open, just bring it to the front.
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var win = new SettingsWindow(_store, _checker, _controller, _startup);
        _settings = win;
        win.Closed += (_, _) => _settings = null;
        win.Saved += async impact =>
        {
            _controller.MarkConfigured();
            if (_controller.State.Status == VpnStatus.Connected)
            {
                // Editing the exception list used to drop the tunnel and re-authenticate for
                // rules openconnect has no part in. Worse than slow: the re-login needs a TOTP
                // from the same 30-second window the last one used, which the gateway rejects.
                if (impact == SaveImpact.SplitDnsOnly)
                {
                    await _controller.RefreshSplitDnsAsync();
                }
                else
                {
                    AppLogger.Shared.Info("settings saved: spawn-time settings changed, reconnecting");
                    await _controller.DisconnectAsync();
                    await _controller.ConnectAsync();
                }
            }
            UpdateTray();
        };
        win.Show();
        win.Activate();
    }

    /// <summary>
    /// Idempotent: Quit, <see cref="Application.SessionEnding"/> and App.OnExit all reach
    /// here, and the controller stop behind it is expensive (it shells out to powershell).
    /// </summary>
    public void StopForShutdown()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        try { _controller.PropertyChanged -= OnControllerPropertyChanged; } catch { /* ignore */ }
        _controller.StopForShutdown();
        AppLogger.Shared.Info("shutdown: disposing tray icon");
        try { _tray?.Dispose(); } catch { /* ignore */ }
        AppLogger.Shared.Info("shutdown: coordinator stopped");
    }

    private void Quit()
    {
        AppLogger.Shared.Info("quit: requested from tray menu");
        Exit();
    }

    /// <summary>
    /// Last resort for the exit path. By the time it can fire, cleanup has long finished, so
    /// the only thing left to cut short is a WPF/tray teardown that never completes — and a
    /// tray app that survives its own Quit, holding the single-instance mutex and forcing the
    /// user into Task Manager, is worse than one that exits abruptly. The timer thread is a
    /// background one, so arming this cannot itself keep the process alive.
    ///
    /// The grace period is well clear of the controller's own bounded waits; if this ever
    /// fires, the preceding "shutdown: …" lines say which step never returned.
    /// </summary>
    private static void ArmForcedExit(TimeSpan grace)
    {
        // Held in a static so the timer is not collected before it fires.
        _exitBackstop = new Timer(
            _ =>
            {
                AppLogger.Shared.Error(
                    $"quit: still running {grace.TotalSeconds:0}s after Quit — forcing exit");
                Environment.Exit(0);
            },
            null, grace, Timeout.InfiniteTimeSpan);
    }
}
