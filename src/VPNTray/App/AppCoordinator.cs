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

    public AppCoordinator()
    {
        var openConnect = new OpenConnectProcess();
        var network = new NetworkMonitor();
        _controller = new VpnController(openConnect, network, () => _store.Load());
        _controller.PropertyChanged += OnControllerPropertyChanged;
    }

    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Application.Current.Dispatcher.Invoke(UpdateTray);

    public void Start()
    {
        _startup.ApplyPreference();
        _tray = new TaskbarIcon { ToolTipText = "VPNTray" };
        BuildMenu();
        _controller.StartNetworkMonitoring();
        Application.Current.SessionEnding += (_, _) => StopForShutdown();

        var config = _store.Load();
        if (config is null || !config.IsConfigured)
        {
            _controller.MarkSetupIncomplete();
            ShowOnboarding();
        }
        UpdateTray();
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
        Add("Check Dependencies…", (_, _) => ShowOnboardingAtDeps());
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

        // Setup-incomplete lock-down: hide connect/disconnect/reconnect/settings.
        bool incomplete = _controller.State.IsSetupIncomplete;
        var items = _tray.ContextMenu.Items;
        foreach (var obj in items)
        {
            if (obj is System.Windows.Controls.MenuItem mi)
            {
                var header = mi.Header?.ToString() ?? "";
                if (header is "Connect" or "Disconnect" or "Reconnect" or "Open Settings…")
                    mi.Visibility = incomplete ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void ShowSettings()
    {
        var win = new SettingsWindow(_store);
        win.Saved += async () =>
        {
            if (_controller.State.Status == VpnStatus.Connected)
            {
                await _controller.DisconnectAsync();
                await _controller.ConnectAsync();
            }
            UpdateTray();
        };
        win.Show();
        win.Activate();
    }

    private void ShowOnboarding()
    {
        var win = new OnboardingWindow(_store, _checker);
        win.Completed += () => { _controller.MarkConfigured(); UpdateTray(); };
        win.Cancelled += () => { if (!( _store.Load()?.IsConfigured ?? false)) _controller.MarkSetupIncomplete(); };
        win.Show();
        win.Activate();
    }

    private void ShowOnboardingAtDeps() => ShowOnboarding();

    public void StopForShutdown()
    {
        try { _controller.PropertyChanged -= OnControllerPropertyChanged; } catch { /* ignore */ }
        _controller.StopForShutdown();
        try { _tray?.Dispose(); } catch { /* ignore */ }
    }

    private void Quit()
    {
        StopForShutdown();
        Application.Current.Shutdown();
    }
}
