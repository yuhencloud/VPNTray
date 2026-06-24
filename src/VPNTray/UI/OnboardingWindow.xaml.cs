using System.Windows;
using System.Windows.Controls;
using VPNTray.Config;
using VPNTray.Dependencies;

namespace VPNTray.UI;

public partial class OnboardingWindow : Window
{
    private readonly ConfigStore _store;
    private readonly DependencyChecker _checker;
    private int _step;
    private bool _completed;

    public event Action? Completed;
    public event Action? Cancelled;

    private readonly StackPanel[] _panes;

    public OnboardingWindow(ConfigStore store, DependencyChecker checker)
    {
        _store = store;
        _checker = checker;
        InitializeComponent();
        _panes = new[] { WelcomePane, DepsPane, CredsPane, DonePane };
        PrefillCredentials();
        ShowStep(0);
        Closing += (_, _) => { if (!_completed) Cancelled?.Invoke(); };
    }

    private void PrefillCredentials()
    {
        var c = _store.Load() ?? new VpnConfig();
        OnbUsername.Text = c.Username;
        OnbPrefix.Password = c.PasswordPrefix;
        OnbTotp.Password = c.TotpSecret;
        OnbGateway.Text = c.Gateway;
        OnbPin.Text = c.ServerCertPin;
    }

    private void ShowStep(int step)
    {
        _step = step;
        for (int i = 0; i < _panes.Length; i++)
            _panes[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == _panes.Length - 1 ? "Done" : "Next";
        if (step == 1) RunDependencyCheck();
    }

    private void RunDependencyCheck()
    {
        var statuses = _checker.Check(_store.Load() ?? new VpnConfig());
        DepsList.ItemsSource = statuses.Select(s =>
            (s.Passed ? "OK  " : "X   ") + s.Name + (s.FixHint is null ? "" : "  — " + s.FixHint));
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => RunDependencyCheck();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(_step - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && !SaveCredentials()) return; // validate before leaving creds
        if (_step == _panes.Length - 1)
        {
            _completed = true;
            Completed?.Invoke();
            Close();
            return;
        }
        ShowStep(_step + 1);
    }

    private bool SaveCredentials()
    {
        var c = new VpnConfig
        {
            Username = OnbUsername.Text,
            PasswordPrefix = OnbPrefix.Password,
            TotpSecret = OnbTotp.Password,
            Gateway = OnbGateway.Text,
            ServerCertPin = OnbPin.Text,
        }.Sanitized();

        if (!c.IsConfigured)
        {
            MessageBox.Show("Please fill in all five fields (username, password prefix, TOTP secret, gateway, cert pin).",
                "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        // Preserve advanced fields from any existing config.
        var existing = _store.Load() ?? new VpnConfig();
        c.OpenconnectPath = existing.OpenconnectPath;
        c.VpncScriptPath = existing.VpncScriptPath;
        c.SkipDnsModification = existing.SkipDnsModification;
        _store.Save(c);
        return true;
    }
}
