using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VPNTray.Config;
using VPNTray.Dependencies;
using VPNTray.Util;

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
    private readonly Border[] _dots;

    public OnboardingWindow(ConfigStore store, DependencyChecker checker)
    {
        _store = store;
        _checker = checker;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        _panes = new[] { WelcomePane, DepsPane, CredsPane, DonePane };
        _dots = new[] { Dot0, Dot1, Dot2, Dot3 };
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

        var accent = (Brush)FindResource("Accent");
        var idle = (Brush)FindResource("InputBorder");
        for (int i = 0; i < _dots.Length; i++)
            _dots[i].Background = i <= step ? accent : idle;

        if (step == 1) RunDependencyCheck();
    }

    private void RunDependencyCheck()
    {
        var statuses = _checker.Check(_store.Load() ?? new VpnConfig());
        DepsList.ItemsSource = statuses
            .Select(s => new DependencyRow { Passed = s.Passed, Name = s.Name, Hint = s.FixHint })
            .ToList();
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => RunDependencyCheck();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();

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

/// <summary>Row shown in the onboarding dependency list (bound by the DepsList DataTemplate).</summary>
public sealed class DependencyRow
{
    public bool Passed { get; set; }
    public string Name { get; set; } = "";
    public string? Hint { get; set; }
}
