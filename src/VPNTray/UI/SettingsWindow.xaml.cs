using System.Windows;
using System.Windows.Input;
using VPNTray.Config;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly DependencyChecker _checker;

    public event Action? Saved;

    public SettingsWindow(ConfigStore store, DependencyChecker checker)
    {
        _store = store;
        _checker = checker;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Load();
        WireChangeTracking();
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
        Saved?.Invoke();
        Close();
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
