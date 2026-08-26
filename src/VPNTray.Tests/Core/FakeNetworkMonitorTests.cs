using VPNTray.Core;

namespace VPNTray.Tests.Core;

public sealed class FakeNetworkMonitor : INetworkMonitor
{
    public event Action<bool>? ReachabilityChanged;
    /// <summary>Defaults to up, matching the environment the pre-existing tests assume.</summary>
    public bool IsAvailable { get; set; } = true;
    public bool Started { get; private set; }
    /// <summary>Lets a test assert the unsubscribe itself, not just an absence of side effects.</summary>
    public bool HasSubscribers => ReachabilityChanged is not null;
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
