using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.UI;

/// <summary>What a saved config asks of a connection that is already up.</summary>
public enum SaveImpact
{
    /// <summary>Only the split-DNS exception lists moved; the tunnel can stay.</summary>
    SplitDnsOnly,

    /// <summary>Something openconnect reads at spawn time changed; the tunnel must be rebuilt.</summary>
    Reconnect,
}

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly DependencyChecker _checker;
    private readonly VpnController _controller;
    private readonly StartupManager _startup;

    // Status-dot colors (match the tray icon set in scripts/make_icons.py).
    private static readonly Brush DotGreen = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush DotOrange = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush DotRed = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush DotGray = Frozen(0x9C, 0xA3, 0xAF);

    public event Action<SaveImpact>? Saved;

    public SettingsWindow(
        ConfigStore store, DependencyChecker checker, VpnController controller, StartupManager startup)
    {
        _store = store;
        _checker = checker;
        _controller = controller;
        _startup = startup;
        InitializeComponent();
        // Attach, not Apply: the DWM effects have to be re-established on every DPI change,
        // not just once at startup.
        WindowEffects.Attach(this);
        VersionText.Text = AppVersion.Display;
        Load();
        WireChangeTracking();

        _controller.PropertyChanged += OnControllerChanged;
        Closed += (_, _) => _controller.PropertyChanged -= OnControllerChanged;
        UpdateConnectUi();

        // Fire-and-forget: it yields at the first await, so the window still paints promptly,
        // and the header stops saying "checking…" a moment later.
        _ = RunToolCheckAsync();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Posts rather than blocks — see the matching note in AppCoordinator. The controller
    /// raises this from thread-pool threads, and a blocking Invoke would park them behind a
    /// UI thread that the exit path is holding.
    /// </summary>
    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        try { Dispatcher.BeginInvoke(UpdateConnectUi); } catch { /* shutting down */ }
    }

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
        PwdBox.Password = c.Password;
        TotpBox.Password = c.TotpSecret;
        AppendOtpRadio.IsChecked = c.AppendOtpToPassword;
        SeparateOtpRadio.IsChecked = !c.AppendOtpToPassword;
        GatewayBox.Text = c.Gateway;
        PinBox.Text = c.ServerCertPin;
        // The stored flag is phrased as an omission ("skip DNS modification"); the UI offers
        // the two destinations instead, which is what the user actually reasons about.
        InternalDnsRadio.IsChecked = !c.SkipDnsModification;
        PublicDnsRadio.IsChecked = c.SkipDnsModification;
        BypassDomainsBox.Text = string.Join(", ", c.SplitDnsPublicNamespaces);
        BypassServersBox.Text = string.Join(", ", c.SplitDnsPublicServers);
        InternalDomainsBox.Text = string.Join(", ", c.SplitDnsInternalNamespaces);
        UpdateDnsSummary();
        // Wires its own handlers, so it must not also be wired in WireChangeTracking.
        SyncLaunchAtLogin();
    }

    private void OnToggleDnsSection(object sender, MouseButtonEventArgs e)
    {
        bool open = DnsPanel.Visibility != Visibility.Visible;
        DnsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DnsChevron.Text = open ? "▾" : "▸";
    }

    /// <summary>
    /// Keeps the collapsed header honest: the section is shut by default, so without this
    /// there is nothing to tell the user which resolver names are going to — or that the
    /// exception list is empty. Both change what resolves.
    /// </summary>
    private void UpdateDnsSummary()
    {
        bool internalMode = InternalDnsRadio.IsChecked == true;
        int exceptions = ParseList(internalMode ? BypassDomainsBox.Text : InternalDomainsBox.Text).Count;
        string mode = internalMode ? "internal DNS" : "public DNS";
        DnsSummary.Text = exceptions == 0
            ? mode
            : $"{mode}, {exceptions} {(exceptions == 1 ? "exception" : "exceptions")}";
    }

    private void WireChangeTracking()
    {
        void Enable() => SaveButton.IsEnabled = true;
        UsernameBox.TextChanged += (_, _) => Enable();
        GatewayBox.TextChanged += (_, _) => Enable();
        PinBox.TextChanged += (_, _) => Enable();
        PwdBox.PasswordChanged += (_, _) => Enable();
        TotpBox.PasswordChanged += (_, _) => Enable();
        AppendOtpRadio.Checked += (_, _) => Enable();
        SeparateOtpRadio.Checked += (_, _) => Enable();
        InternalDnsRadio.Checked += (_, _) => { Enable(); UpdateDnsSummary(); };
        PublicDnsRadio.Checked += (_, _) => { Enable(); UpdateDnsSummary(); };
        BypassDomainsBox.TextChanged += (_, _) => { Enable(); UpdateDnsSummary(); };
        BypassServersBox.TextChanged += (_, _) => Enable();
        InternalDomainsBox.TextChanged += (_, _) => { Enable(); UpdateDnsSummary(); };
    }

    /// <summary>Accepts comma, semicolon, or whitespace between entries.</summary>
    private static List<string> ParseList(string text)
        => text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .ToList();

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

    /// <summary>
    /// Expanding only reveals the detail — it does not re-run. Re-checking is what the
    /// Re-check button is for, and hanging a process spawn off an expand would make the
    /// button look decorative while hiding the real trigger in an invisible gesture.
    /// </summary>
    private void OnToggleStartupSection(object sender, MouseButtonEventArgs e)
    {
        bool open = StartupPanel.Visibility != Visibility.Visible;
        StartupPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        StartupChevron.Text = open ? "▾" : "▸";
    }

    /// <summary>
    /// Applies straight away rather than waiting for Save. This preference does not live in the
    /// config file at all — it is a registry value plus a registered scheduled task — so routing
    /// it through Save would imply a rollback story that does not exist.
    ///
    /// Off the UI thread because it shells out to schtasks. The checkbox is then reset from what
    /// <see cref="StartupManager.IsEnabled"/> actually reports, so a failed schtasks call shows
    /// up as the box springing back instead of a UI that quietly disagrees with the machine.
    /// </summary>
    private async Task ApplyLaunchAtLoginAsync(bool enable)
    {
        LaunchAtLoginBox.IsEnabled = false;
        try
        {
            await Task.Run(() => { if (enable) _startup.Enable(); else _startup.Disable(); });
        }
        finally
        {
            LaunchAtLoginBox.IsEnabled = true;
            SyncLaunchAtLogin();
        }
    }

    /// <summary>Reflects the machine's actual state, not what was last clicked.</summary>
    private void SyncLaunchAtLogin()
    {
        bool on = _startup.IsEnabled();
        bool connect = _startup.IsConnectAtLoginEnabled();
        // Detached while writing, or the write would re-enter the handlers.
        LaunchAtLoginBox.Checked -= OnLaunchAtLoginChanged;
        LaunchAtLoginBox.Unchecked -= OnLaunchAtLoginChanged;
        ConnectAtLoginBox.Checked -= OnConnectAtLoginChanged;
        ConnectAtLoginBox.Unchecked -= OnConnectAtLoginChanged;
        LaunchAtLoginBox.IsChecked = on;
        ConnectAtLoginBox.IsChecked = connect;
        LaunchAtLoginBox.Checked += OnLaunchAtLoginChanged;
        LaunchAtLoginBox.Unchecked += OnLaunchAtLoginChanged;
        ConnectAtLoginBox.Checked += OnConnectAtLoginChanged;
        ConnectAtLoginBox.Unchecked += OnConnectAtLoginChanged;

        StartupSummary.Text = !on ? "manual start"
            : connect ? "launches at login, auto-connects"
            : "launches at login";
    }

    private async void OnLaunchAtLoginChanged(object sender, RoutedEventArgs e)
        => await ApplyLaunchAtLoginAsync(LaunchAtLoginBox.IsChecked == true);

    /// <summary>A bare registry write — instant, so no disable/re-enable dance is needed.</summary>
    private void OnConnectAtLoginChanged(object sender, RoutedEventArgs e)
    {
        _startup.SetConnectAtLogin(ConnectAtLoginBox.IsChecked == true);
        SyncLaunchAtLogin();
    }

    private void OnToggleToolsSection(object sender, MouseButtonEventArgs e)
    {
        bool open = ToolsPanel.Visibility != Visibility.Visible;
        ToolsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ToolsChevron.Text = open ? "▾" : "▸";
    }

    private async void OnRecheckTools(object sender, RoutedEventArgs e) => await RunToolCheckAsync();

    /// <summary>
    /// Fills the tools list and the verdict in its header. Run once when the window opens, so
    /// the collapsed header says something, and then on demand.
    ///
    /// Off the UI thread: one of the checks shells out to `openconnect --version`, and a
    /// process spawn on the dispatcher is a visible hitch in a window opened from a tray click.
    /// </summary>
    private async Task RunToolCheckAsync()
    {
        RecheckButton.IsEnabled = false;
        SetToolsSummary("checking…", failed: false);
        try
        {
            // Sanitized() so the report reflects the paths Connect will really use, including
            // the bundled fallback when the saved absolute path no longer exists.
            var config = (_store.Load() ?? new VpnConfig()).Sanitized();
            var statuses = await Task.Run(() => _checker.Check(config));

            DepsList.ItemsSource = statuses
                .Select(s => new DependencyRow { Passed = s.Passed, Name = s.Name, Hint = s.FixHint })
                .ToList();

            int failed = statuses.Count(s => !s.Passed);
            SetToolsSummary(
                failed == 0 ? $"{statuses.Count} checks passed" : $"{failed} of {statuses.Count} failed",
                failed > 0);
        }
        finally
        {
            RecheckButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Colours a failure red rather than auto-expanding the section: a broken tool means the
    /// app cannot connect at all, so it has to be visible while collapsed, but the section
    /// still opens only when the user asks — same as DNS.
    /// </summary>
    private void SetToolsSummary(string text, bool failed)
    {
        ToolsSummary.Text = text;
        ToolsSummary.Foreground = (Brush)FindResource(failed ? "ErrRed" : "TextSecondary");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var bypassDomains = ParseList(BypassDomainsBox.Text);
        var bypassServers = ParseList(BypassServersBox.Text);
        var internalDomains = ParseList(InternalDomainsBox.Text);

        // Each mode's exceptions are validated only while that mode is selected. The other
        // mode's fields are greyed out, and refusing a save over a field the user cannot edit
        // is a dead end; the entries are still stored, so they get validated on the save that
        // switches back.
        if (InternalDnsRadio.IsChecked == true)
        {
            if (!PublicExceptionsAreValid(bypassDomains, bypassServers)) return;
        }
        else if (!InternalExceptionsAreValid(internalDomains)) return;

        // Edit the credential and DNS fields; preserve the advanced ones
        // (openconnect / vpnc-script paths) from the saved config.
        var existing = _store.Load() ?? new VpnConfig();
        var c = new VpnConfig
        {
            Username = UsernameBox.Text,
            Password = PwdBox.Password,
            TotpSecret = TotpBox.Password,
            AppendOtpToPassword = SeparateOtpRadio.IsChecked != true,
            Gateway = GatewayBox.Text,
            ServerCertPin = PinBox.Text,
            OpenconnectPath = existing.OpenconnectPath,
            VpncScriptPath = existing.VpncScriptPath,
            SkipDnsModification = InternalDnsRadio.IsChecked != true,
            SplitDnsPublicNamespaces = bypassDomains,
            SplitDnsPublicServers = bypassServers,
            SplitDnsInternalNamespaces = internalDomains,
            // SchemaVersion intentionally left at the default: saving rewrites the file in
            // the current schema, so a migrated v1 config must not keep claiming v1.
        }.Sanitized();

        if (!c.IsConfigured)
        {
            MessageBox.Show(this,
                "Please fill in all five fields (username, password, TOTP secret, gateway, cert pin).",
                "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _store.Save(c);
        SaveButton.IsEnabled = false;
        // Sanitized() may have dropped entries the parser kept, so re-read rather than
        // leaving the boxes showing something the config no longer contains.
        BypassDomainsBox.Text = string.Join(", ", c.SplitDnsPublicNamespaces);
        BypassServersBox.Text = string.Join(", ", c.SplitDnsPublicServers);
        InternalDomainsBox.Text = string.Join(", ", c.SplitDnsInternalNamespaces);
        UpdateDnsSummary();
        // Compared sanitized-to-sanitized, or path resolution alone would read as a change and
        // force a needless reconnect.
        var impact = existing.Sanitized().TunnelSettingsMatch(c)
            ? SaveImpact.SplitDnsOnly
            : SaveImpact.Reconnect;
        // Keep the window open so the user can connect from here.
        Saved?.Invoke(impact);
        UpdateConnectUi();
    }

    /// <summary>
    /// Reports the first problem with the public-DNS exceptions, if any. False means the save
    /// was refused and the user has been told why.
    /// </summary>
    private bool PublicExceptionsAreValid(List<string> domains, List<string> servers)
    {
        if (!DomainsAreValid(domains, "Exception domains")) return false;

        var badServers = servers.Where(s => !SplitDnsManager.IsValidServer(s)).ToList();
        if (badServers.Count > 0)
            return RejectDnsSetting(
                "These are not valid IP addresses:\n\n" + string.Join("\n", badServers) +
                "\n\nUse entries like 8.8.8.8 or 2001:4860:4860::8888.", "Exception resolvers");

        // A domain with nowhere to send it installs no rule at all, so it would silently keep
        // resolving through the internal resolver — exactly the breakage being avoided.
        if (domains.Count > 0 && servers.Count == 0)
            return RejectDnsSetting(
                "The exception domains need at least one resolver to go to.\n\n" +
                "Fill in \"Resolved through\" (e.g. 8.8.8.8), or clear the domain list.",
                "Exception resolvers");

        return true;
    }

    /// <summary>
    /// The public-DNS-mode counterpart. There is no resolver list to check: those come from the
    /// gateway at connect time, which is also why an empty list here is not an error — it just
    /// means nothing is redirected into the tunnel.
    /// </summary>
    private bool InternalExceptionsAreValid(List<string> domains)
    {
        if (!DomainsAreValid(domains, "Internal domains")) return false;

        // Asked, not refused: ".internal" and ".corp" have the same shape as ".com" and are
        // real private TLDs, so this cannot be a validation rule. It is worth one question
        // because the rule it writes is machine-wide and the typo is silent otherwise.
        var wholeTlds = domains.Where(SplitDnsManager.CoversAnEntireTld).ToList();
        if (wholeTlds.Count > 0)
        {
            if (DnsPanel.Visibility != Visibility.Visible)
            {
                DnsPanel.Visibility = Visibility.Visible;
                DnsChevron.Text = "▾";
            }
            var answer = MessageBox.Show(this,
                "These cover a whole top-level domain:\n\n" + string.Join("\n", wholeTlds) +
                "\n\nEvery name under them will resolve through the VPN, for every account on " +
                "this machine. That is right for a private TLD such as .internal, and wrong for " +
                "a public one such as .com.\n\nSave anyway?",
                "Internal domains", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
        }

        return true;
    }

    private bool DomainsAreValid(List<string> domains, string title)
    {
        var malformed = domains.Where(d => !SplitDnsManager.IsValidNamespace(d)).ToList();
        if (malformed.Count > 0)
            return RejectDnsSetting(
                "These are not valid domain suffixes:\n\n" + string.Join("\n", malformed) +
                "\n\nUse entries like .example.com or api.example.com.", title);
        return true;
    }

    /// <summary>
    /// Refuses a save over a DNS setting and says why. Opens the section first: it is
    /// collapsed by default, and a complaint about a field the user cannot see is a dead end.
    /// Always returns false so callers can `return RejectDnsSetting(…)`.
    /// </summary>
    private bool RejectDnsSetting(string message, string title)
    {
        if (DnsPanel.Visibility != Visibility.Visible)
        {
            DnsPanel.Visibility = Visibility.Visible;
            DnsChevron.Text = "▾";
        }
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
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
