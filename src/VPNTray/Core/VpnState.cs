namespace VPNTray.Core;

public enum VpnStatus { Disconnected, Connecting, Connected, Failed }

public sealed record VpnState(VpnStatus Status, DateTimeOffset? Since = null, string? Reason = null)
{
    public const string SetupIncompletePrefix = "Setup incomplete";

    public static VpnState Disconnected { get; } = new(VpnStatus.Disconnected);
    public static VpnState Connecting { get; } = new(VpnStatus.Connecting);
    public static VpnState Connected(DateTimeOffset since) => new(VpnStatus.Connected, Since: since);
    public static VpnState Failed(string reason) => new(VpnStatus.Failed, Reason: reason);

    public bool IsSetupIncomplete =>
        Status == VpnStatus.Failed && (Reason?.StartsWith(SetupIncompletePrefix) ?? false);
}
