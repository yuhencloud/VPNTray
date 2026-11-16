using System.Text.Json.Serialization;
using VPNTray.Dependencies;

namespace VPNTray.Config;

public class VpnConfig
{
    public string Username { get; set; } = "";

    /// <summary>
    /// The static login password, without any OTP. How the OTP reaches the gateway is
    /// decided by <see cref="AppendOtpToPassword"/>.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// How the gateway wants the one-time code:
    /// <list type="bullet">
    /// <item><c>true</c> — one prompt, password and current 6-digit code concatenated with
    /// nothing between them (<c>Abc#2026</c> + <c>123456</c> → <c>Abc#2026123456</c>). The app
    /// computes the code and sends the combined string on stdin.</item>
    /// <item><c>false</c> — two prompts: the password first, then a separate OTP challenge
    /// that openconnect answers itself from <see cref="TotpSecret"/>.</item>
    /// </list>
    /// ON by default because that is what the gateway switched to; a config file written
    /// before this setting existed picks up the default and keeps connecting without a visit
    /// to Settings.
    /// </summary>
    public bool AppendOtpToPassword { get; set; } = true;

    /// <summary>
    /// Migration shim for schema v1, where this field held the static part of a
    /// concatenated `prefix + TOTP` password — which is exactly today's password.
    /// Read-only: deserializes old configs, never written back out.
    /// </summary>
    [JsonPropertyName("PasswordPrefix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPasswordPrefix
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(Password)) Password = value;
        }
    }

    public string TotpSecret { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string ServerCertPin { get; set; } = "";
    public string OpenconnectPath { get; set; } = BundledBinaries.OpenconnectExe;
    public string VpncScriptPath { get; set; } = BundledBinaries.VpncScript;

    /// <summary>
    /// Run openconnect against the --no-dns vpnc-script variant, leaving the tunnel adapter
    /// without the gateway's resolvers — every lookup keeps using the system's normal (public)
    /// DNS. ON by default, a deliberate trade-off measured in the field: the fleet's internal
    /// resolver answers several public names (download hosts among them) with in-cloud
    /// addresses the tunnel does not route, so internal-DNS-by-default broke downloads out of
    /// the box, which is worse than *.corp names not resolving until the user flips one radio
    /// button in Settings → DNS. Whoever needs internal names switches modes and, for the
    /// hosts the internal resolver answers wrongly, adds
    /// <see cref="SplitDnsPublicNamespaces"/> exceptions.
    /// </summary>
    public bool SkipDnsModification { get; set; } = true;

    /// <summary>
    /// DNS suffixes forced onto <see cref="SplitDnsPublicServers"/> via NRPT while connected.
    /// A leading dot means "this suffix and everything under it". See
    /// <see cref="Core.SplitDnsManager"/> for the failure this prevents. Ignored when
    /// <see cref="SkipDnsModification"/> is on, since nothing points at the internal resolver then.
    ///
    /// Empty by default, deliberately. Which public names a given gateway's resolver answers
    /// wrongly is a property of that deployment, and an NRPT rule is machine-wide: shipping a
    /// guess means every install silently redirects somebody's lookups to a third-party
    /// resolver for a problem it may not have. Opt in per site.
    /// </summary>
    public List<string> SplitDnsPublicNamespaces { get; set; } = new();

    /// <summary>Resolvers for <see cref="SplitDnsPublicNamespaces"/>. Empty by default, for the same reason.</summary>
    public List<string> SplitDnsPublicServers { get; set; } = new();

    /// <summary>
    /// The mirror image of <see cref="SplitDnsPublicNamespaces"/>, for the other mode: DNS
    /// suffixes sent to the tunnel's own resolvers via NRPT while
    /// <see cref="SkipDnsModification"/> is on. Lets the default public-DNS mode keep answering
    /// everything else, so internal names resolve without handing the whole system over to the
    /// internal resolver and re-breaking the public names that made public DNS the default.
    ///
    /// There is no companion server list, on purpose. The resolvers are the ones the gateway
    /// pushes at connect time — per session, and not something a user can be expected to know
    /// or keep current. They arrive from the --no-dns vpnc-script via
    /// <see cref="Core.IOpenConnectProcessRunning.TunnelDnsServersAsync"/>.
    ///
    /// Empty by default: which internal suffixes exist is a property of the deployment, and an
    /// NRPT rule is machine-wide.
    /// </summary>
    public List<string> SplitDnsInternalNamespaces { get; set; } = new();

    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Username) &&
        !string.IsNullOrEmpty(Password) &&
        !string.IsNullOrEmpty(TotpSecret) &&
        !string.IsNullOrEmpty(Gateway) &&
        !string.IsNullOrEmpty(ServerCertPin);

    public VpnConfig Sanitized() => new()
    {
        Username = Clean(Username),
        Password = Clean(Password),
        AppendOtpToPassword = AppendOtpToPassword,
        TotpSecret = Clean(TotpSecret),
        Gateway = Clean(Gateway),
        ServerCertPin = Clean(ServerCertPin),
        OpenconnectPath = ResolveNativeTool(Clean(OpenconnectPath), BundledBinaries.OpenconnectExe),
        VpncScriptPath = ResolveNativeTool(Clean(VpncScriptPath), BundledBinaries.VpncScript),
        SkipDnsModification = SkipDnsModification,
        SplitDnsPublicNamespaces = CleanList(SplitDnsPublicNamespaces),
        SplitDnsPublicServers = CleanList(SplitDnsPublicServers),
        SplitDnsInternalNamespaces = CleanList(SplitDnsInternalNamespaces),
        SchemaVersion = SchemaVersion,
    };

    /// <summary>
    /// True when nothing openconnect reads at spawn time differs — credentials, OTP mode, gateway, pin,
    /// the two native-tool paths, and the DNS mode (which picks the vpnc-script variant).
    ///
    /// All three suffix lists are deliberately excluded: they become NRPT rules applied after
    /// the handshake, independent of the tunnel, so they can be swapped underneath a live
    /// connection. That is worth distinguishing — rebuilding the tunnel means re-authenticating,
    /// and openconnect would have to do it with a TOTP from the same 30-second window the last
    /// login just consumed, which gateways with replay protection reject.
    /// </summary>
    public bool TunnelSettingsMatch(VpnConfig other) =>
        Username == other.Username &&
        Password == other.Password &&
        AppendOtpToPassword == other.AppendOtpToPassword &&
        TotpSecret == other.TotpSecret &&
        Gateway == other.Gateway &&
        ServerCertPin == other.ServerCertPin &&
        OpenconnectPath == other.OpenconnectPath &&
        VpncScriptPath == other.VpncScriptPath &&
        SkipDnsModification == other.SkipDnsModification;

    /// <summary>
    /// Bring a just-deserialized config up to <see cref="CurrentSchemaVersion"/>. Mutates,
    /// so call it only on an instance nobody else holds — i.e. straight out of the parser.
    ///
    /// Stamps the version and nothing else. An earlier revision forced
    /// <see cref="SkipDnsModification"/> off for pre-v3 files, back when the default flipped to
    /// internal DNS; the default has since flipped back (see the property), and an explicit
    /// value on disk — whichever way it points — is the user's setting, not something an
    /// upgrade gets to overrule.
    /// </summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>
    /// Native-tool paths are absolute and were saved by whichever install wrote the config,
    /// so uninstalling or moving the app leaves them pointing at nothing — and the only
    /// symptom is an opaque "file not found" when spawning. Fall back to the copy shipped
    /// next to the running executable, which is the one we actually know is there.
    /// </summary>
    private static string ResolveNativeTool(string configured, string bundled)
        => File.Exists(configured) ? configured : bundled;

    private static readonly HashSet<char> Invisible = new()
    {
        '\u200B', '\u200C', '\u200D', '\uFEFF',
        '\u2028', '\u2029', '\u00A0', '\u3000',
    };

    /// <summary>Cleans each entry and drops the ones that clean away to nothing.</summary>
    private static List<string> CleanList(IEnumerable<string>? values)
        => (values ?? Enumerable.Empty<string>())
            .Select(v => Clean(v ?? ""))
            .Where(v => v.Length > 0)
            .ToList();

    private static string Clean(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (Invisible.Contains(ch)) continue;
            if (ch < 0x20 || ch == 0x7F) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
