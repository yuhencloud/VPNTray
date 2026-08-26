namespace VPNTray.Core;

public interface INetworkMonitor
{
    /// <summary>Fires with `true` when the network becomes reachable, `false` when it is lost.</summary>
    event Action<bool>? ReachabilityChanged;

    /// <summary>
    /// Whether any network is up right now — the poll to the event's push. Callers about to
    /// wait on <see cref="ReachabilityChanged"/> check this first: a network that was already
    /// up before they subscribed fires no event.
    /// </summary>
    bool IsAvailable { get; }

    void Start();
    void Stop();
}
