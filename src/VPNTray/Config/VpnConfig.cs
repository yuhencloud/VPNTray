using System.Text.Json.Serialization;
using VPNTray.Dependencies;

namespace VPNTray.Config;

public class VpnConfig
{
    public string Username { get; set; } = "";

    /// <summary>
    /// The full login password. The gateway asks for it first; the OTP is a separate
    /// second prompt that openconnect answers from <see cref="TotpSecret"/>.
    /// </summary>
    public string Password { get; set; } = "";

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
    /// without the gateway's resolvers. Off by default: with it on, no internal name resolves
    /// at all (*.corp.* returns NXDOMAIN from the public resolver). The public-domain
    /// breakage it used to work around is handled by
    /// <see cref="SplitDnsPublicNamespaces"/> instead, which is surgical rather than total.
    /// </summary>
    public bool SkipDnsModification { get; set; }

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
        TotpSecret = Clean(TotpSecret),
        Gateway = Clean(Gateway),
        ServerCertPin = Clean(ServerCertPin),
        OpenconnectPath = ResolveNativeTool(Clean(OpenconnectPath), BundledBinaries.OpenconnectExe),
        VpncScriptPath = ResolveNativeTool(Clean(VpncScriptPath), BundledBinaries.VpncScript),
        SkipDnsModification = SkipDnsModification,
        SplitDnsPublicNamespaces = CleanList(SplitDnsPublicNamespaces),
        SplitDnsPublicServers = CleanList(SplitDnsPublicServers),
        SchemaVersion = SchemaVersion,
    };

    /// <summary>
    /// True when nothing openconnect reads at spawn time differs — credentials, gateway, pin,
    /// the two native-tool paths, and the DNS mode (which picks the vpnc-script variant).
    ///
    /// The split-DNS exception lists are deliberately excluded: they become NRPT rules applied
    /// after the handshake, independent of the tunnel, so they can be swapped underneath a live
    /// connection. That is worth distinguishing — rebuilding the tunnel means re-authenticating,
    /// and openconnect would have to do it with a TOTP from the same 30-second window the last
    /// login just consumed, which gateways with replay protection reject.
    /// </summary>
    public bool TunnelSettingsMatch(VpnConfig other) =>
        Username == other.Username &&
        Password == other.Password &&
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
    /// v2 shipped with <see cref="SkipDnsModification"/> defaulted on, which meant no
    /// internal name ever resolved. Because the key is written out explicitly, a new default
    /// alone would not reach configs already on disk; they have to be flipped on load.
    /// </summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        SkipDnsModification = false;
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
