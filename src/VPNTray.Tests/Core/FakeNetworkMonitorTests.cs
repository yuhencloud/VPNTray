using VPNTray.Core;

namespace VPNTray.Tests.Core;

public sealed class FakeNetworkMonitor : INetworkMonitor
{
    public event Action<bool>? ReachabilityChanged;
    public bool Started { get; private set; }
    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void Raise(bool reachable) => ReachabilityChanged?.Invoke(reachable);
}

public class FakeNetworkMonitorTests
{
    [Fact]
    public void Raise_InvokesSubscribers()
    {
        var m = new FakeNetworkMonitor();
        bool? got = null;
        m.ReachabilityChanged += r => got = r;
        m.Start();
        m.Raise(true);
        Assert.True(m.Started);
        Assert.Equal(true, got);
    }
}
