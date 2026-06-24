using System.Windows;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly StartupManager _startup;

    public event Action? Saved;

    public SettingsWindow(ConfigStore store, StartupManager startup)
    {
        _store = store;
        _startup = startup;
        InitializeComponent();
        Load();
        WireChangeTracking();
    }

    private void Load()
    {
        var c = _store.Load() ?? new VpnConfig();
        UsernameBox.Text = c.Username;
        PrefixBox.Password = c.PasswordPrefix;
        TotpBox.Password = c.TotpSecret;
        GatewayBox.Text = c.Gateway;
        PinBox.Text = c.ServerCertPin;
        OpenconnectBox.Text = c.OpenconnectPath;
        ScriptBox.Text = c.VpncScriptPath;
        SkipDnsBox.IsChecked = c.SkipDnsModification;
        LaunchAtLoginBox.IsChecked = _startup.IsEnabled();
    }

    private void WireChangeTracking()
    {
        void Enable() => SaveButton.IsEnabled = true;
        UsernameBox.TextChanged += (_, _) => Enable();
        GatewayBox.TextChanged += (_, _) => Enable();
        PinBox.TextChanged += (_, _) => Enable();
        OpenconnectBox.TextChanged += (_, _) => Enable();
        ScriptBox.TextChanged += (_, _) => Enable();
        PrefixBox.PasswordChanged += (_, _) => Enable();
        TotpBox.PasswordChanged += (_, _) => Enable();
        SkipDnsBox.Click += (_, _) => Enable();
        LaunchAtLoginBox.Click += (_, _) => Enable();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var c = new VpnConfig
        {
            Username = UsernameBox.Text,
            PasswordPrefix = PrefixBox.Password,
            TotpSecret = TotpBox.Password,
            Gateway = GatewayBox.Text,
            ServerCertPin = PinBox.Text,
            OpenconnectPath = OpenconnectBox.Text,
            VpncScriptPath = ScriptBox.Text,
            SkipDnsModification = SkipDnsBox.IsChecked == true,
        }.Sanitized();

        _store.Save(c);
        if (LaunchAtLoginBox.IsChecked == true) _startup.Enable(); else _startup.Disable();
        Saved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
