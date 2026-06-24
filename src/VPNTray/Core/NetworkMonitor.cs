using System.Net.NetworkInformation;

namespace VPNTray.Core;

public sealed class NetworkMonitor : INetworkMonitor
{
    public event Action<bool>? ReachabilityChanged;

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public void Stop()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => ReachabilityChanged?.Invoke(e.IsAvailable);
}
