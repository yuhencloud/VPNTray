using VPNTray.Config;

namespace VPNTray.Core;

public interface IOpenConnectProcessRunning
{
    /// <summary>
    /// Spawn openconnect and write "password\n" to its stdin. That answers only the first
    /// auth stage; the separate OTP prompt is answered by openconnect from
    /// <see cref="VpnConfig.TotpSecret"/>. Returns after spawn, not handshake.
    /// </summary>
    void Start(VpnConfig config, string password);

    /// <summary>Resolve within `timeout`; returns connected or failed(reason).</summary>
    Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout);

    bool IsRunning();

    /// <summary>Terminate the spawned openconnect. Idempotent.</summary>
    void Stop();
}
