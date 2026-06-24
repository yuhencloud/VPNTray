using System.Windows;
using System.Windows.Input;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;

    public event Action? Saved;

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();

    public SettingsWindow(ConfigStore store)
    {
        _store = store;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Load();
        WireChangeTracking();
    }

    private void Load()
    {
        var c = _store.Load() ?? new VpnConfig();
        UsernameBox.Text = c.Username;
        GatewayBox.Text = c.Gateway;
    }

    private void WireChangeTracking()
    {
        void Enable() => SaveButton.IsEnabled = true;
        UsernameBox.TextChanged += (_, _) => Enable();
        GatewayBox.TextChanged += (_, _) => Enable();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Settings edits only Username + Gateway; preserve everything else
        // (password prefix, TOTP secret, cert pin, paths, flags) from the saved config.
        var c = _store.Load() ?? new VpnConfig();
        c.Username = UsernameBox.Text;
        c.Gateway = GatewayBox.Text;
        _store.Save(c.Sanitized());
        Saved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
