using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly DependencyChecker _checker;
    private readonly VpnController _controller;

    // Status-dot colors (match the tray icon set in scripts/make_icons.py).
    private static readonly Brush DotGreen = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush DotOrange = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush DotRed = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush DotGray = Frozen(0x9C, 0xA3, 0xAF);

    public event Action? Saved;

    public SettingsWindow(ConfigStore store, DependencyChecker checker, VpnController controller)
    {
        _store = store;
        _checker = checker;
        _controller = controller;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Load();
        WireChangeTracking();

        _controller.PropertyChanged += OnControllerChanged;
        Closed += (_, _) => _controller.PropertyChanged -= OnControllerChanged;
        UpdateConnectUi();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
        => Dispatcher.Invoke(UpdateConnectUi);

    private void UpdateConnectUi()
    {
        var status = _controller.State.Status;
        bool configured = _store.Load()?.IsConfigured ?? false;

        switch (status)
        {
            case VpnStatus.Connected:
                StatusDot.Fill = DotGreen;
                StatusText.Text = "Connected";
                ConnectButton.Content = "Disconnect";
                ConnectButton.IsEnabled = true;
                break;
            case VpnStatus.Connecting:
                StatusDot.Fill = DotOrange;
                StatusText.Text = "Connecting…";
                ConnectButton.Content = "Connecting…";
                ConnectButton.IsEnabled = false;
                break;
            case VpnStatus.Failed when _controller.State.IsSetupIncomplete:
                StatusDot.Fill = DotGray;
                StatusText.Text = "Not configured";
                ConnectButton.Content = "Connect";
                ConnectButton.IsEnabled = configured;
                break;
            case VpnStatus.Failed:
                StatusDot.Fill = DotRed;
                StatusText.Text = "Failed";
                ConnectButton.Content = "Connect";
                ConnectButton.IsEnabled = configured;
                break;
            default:
                StatusDot.Fill = DotGray;
                StatusText.Text = "Disconnected";
                ConnectButton.Content = "Connect";
                ConnectButton.IsEnabled = configured;
                break;
        }
    }

    private async void OnConnectToggle(object sender, RoutedEventArgs e)
    {
        if (_controller.State.Status == VpnStatus.Connected)
            await _controller.DisconnectAsync();
        else
            await _controller.ConnectAsync();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();

    private void Load()
    {
        var c = _store.Load() ?? new VpnConfig();
        UsernameBox.Text = c.Username;
        PrefixBox.Password = c.PasswordPrefix;
        TotpBox.Password = c.TotpSecret;
        GatewayBox.Text = c.Gateway;
        PinBox.Text = c.ServerCertPin;
    }

    private void WireChangeTracking()
    {
        void Enable() => SaveButton.IsEnabled = true;
        UsernameBox.TextChanged += (_, _) => Enable();
        GatewayBox.TextChanged += (_, _) => Enable();
        PinBox.TextChanged += (_, _) => Enable();
        PrefixBox.PasswordChanged += (_, _) => Enable();
        TotpBox.PasswordChanged += (_, _) => Enable();
    }

    private void OnImportQrFile(object sender, RoutedEventArgs e)
    {
        var secret = QrImport.FromImageFile(this);
        if (secret is not null) ApplySecret(secret);
    }

    private async void OnScanQrScreen(object sender, RoutedEventArgs e)
    {
        var secret = await QrImport.FromScreen(this);
        if (secret is not null) ApplySecret(secret);
    }

    private void ApplySecret(string secret)
    {
        TotpBox.Password = secret;
        MessageBox.Show(this, "TOTP secret imported.", "QR import",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCheckTools(object sender, RoutedEventArgs e)
    {
        var statuses = _checker.Check(_store.Load() ?? new VpnConfig());
        DepsList.ItemsSource = statuses
            .Select(s => new DependencyRow { Passed = s.Passed, Name = s.Name, Hint = s.FixHint })
            .ToList();
        DepsList.Visibility = Visibility.Visible;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Edit the five credential fields; preserve advanced fields
        // (openconnect / vpnc-script paths, Skip DNS flag) from the saved config.
        var existing = _store.Load() ?? new VpnConfig();
        var c = new VpnConfig
        {
            Username = UsernameBox.Text,
            PasswordPrefix = PrefixBox.Password,
            TotpSecret = TotpBox.Password,
            Gateway = GatewayBox.Text,
            ServerCertPin = PinBox.Text,
            OpenconnectPath = existing.OpenconnectPath,
            VpncScriptPath = existing.VpncScriptPath,
            SkipDnsModification = existing.SkipDnsModification,
            SchemaVersion = existing.SchemaVersion,
        }.Sanitized();

        if (!c.IsConfigured)
        {
            MessageBox.Show(this,
                "Please fill in all five fields (username, password prefix, TOTP secret, gateway, cert pin).",
                "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _store.Save(c);
        SaveButton.IsEnabled = false;
        // Keep the window open so the user can connect from here.
        Saved?.Invoke();
        UpdateConnectUi();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Row shown in the bundled-tools check list (bound by the DepsList DataTemplate).</summary>
public sealed class DependencyRow
{
    public bool Passed { get; set; }
    public string Name { get; set; } = "";
    public string? Hint { get; set; }
}
