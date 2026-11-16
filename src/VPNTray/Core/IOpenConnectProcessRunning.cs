using VPNTray.Config;

namespace VPNTray.Core;

public interface IOpenConnectProcessRunning
{
    /// <summary>
    /// Spawn openconnect and write "password\n" to its stdin. With
    /// <see cref="VpnConfig.AppendOtpToPassword"/> on, <paramref name="password"/> already
    /// ends in the current OTP; otherwise it answers only the first auth stage and the
    /// separate OTP prompt is answered by openconnect from <see cref="VpnConfig.TotpSecret"/>.
    /// Returns after spawn, not handshake. <paramref name="pinnedAddress"/>, when set, is the
    /// gateway address to connect to instead of the one DNS picks.
    /// </summary>
    void Start(VpnConfig config, string password, string? pinnedAddress = null);

    /// <summary>Resolve within `timeout`; returns connected or failed(reason).</summary>
    Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout);

    bool IsRunning();

    /// <summary>
    /// The resolvers the gateway pushed for the current session, in the order it sent them.
    /// Empty when none were reported — before the first handshake, after a failed one, or
    /// whenever the vpnc-script variant in use does not report them.
    ///
    /// They exist because public-DNS mode leaves the tunnel adapter without DNS, so the only
    /// way to resolve an internal suffix is an NRPT rule pointing it at these. Anything read
    /// from here reaches a command line, so treat it as untrusted and validate before use.
    ///
    /// Ask only when about to use them, and expect the first call to block briefly: nothing
    /// orders the report against the handshake, so a call made too early has to wait for it.
    /// Callers may retry — the answer is re-checked while it is still empty, so a suffix list
    /// added mid-session does not have to wait for a reconnect to get its resolvers.
    /// </summary>
    Task<IReadOnlyList<string>> TunnelDnsServersAsync();

    /// <summary>Terminate the spawned openconnect. Idempotent.</summary>
    void Stop();
}
