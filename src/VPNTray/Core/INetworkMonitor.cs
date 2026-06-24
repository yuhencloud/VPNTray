namespace VPNTray.Core;

public interface INetworkMonitor
{
    /// <summary>Fires with `true` when the network becomes reachable, `false` when it is lost.</summary>
    event Action<bool>? ReachabilityChanged;
    void Start();
    void Stop();
}
