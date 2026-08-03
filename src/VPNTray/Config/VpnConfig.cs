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
    public bool SkipDnsModification { get; set; } = true;
    public int SchemaVersion { get; set; } = 2;

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
        SchemaVersion = SchemaVersion,
    };

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
