using VPNTray.Dependencies;

namespace VPNTray.Config;

public class VpnConfig
{
    public string Username { get; set; } = "";
    public string PasswordPrefix { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string ServerCertPin { get; set; } = "";
    public string OpenconnectPath { get; set; } = BundledBinaries.OpenconnectExe;
    public string VpncScriptPath { get; set; } = BundledBinaries.VpncScript;
    public bool SkipDnsModification { get; set; } = true;
    public int SchemaVersion { get; set; } = 1;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Username) &&
        !string.IsNullOrEmpty(PasswordPrefix) &&
        !string.IsNullOrEmpty(TotpSecret) &&
        !string.IsNullOrEmpty(Gateway) &&
        !string.IsNullOrEmpty(ServerCertPin);

    public VpnConfig Sanitized() => new()
    {
        Username = Clean(Username),
        PasswordPrefix = Clean(PasswordPrefix),
        TotpSecret = Clean(TotpSecret),
        Gateway = Clean(Gateway),
        ServerCertPin = Clean(ServerCertPin),
        OpenconnectPath = Clean(OpenconnectPath),
        VpncScriptPath = Clean(VpncScriptPath),
        SkipDnsModification = SkipDnsModification,
        SchemaVersion = SchemaVersion,
    };

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
