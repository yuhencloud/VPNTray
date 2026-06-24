using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class StartupManagerTests
{
    [Fact]
    public void BuildCreateArgs_RequestsHighestPrivilegesAndLogonTrigger()
    {
        var args = StartupManager.BuildCreateArgs("VPNTray", @"C:\Apps\VPNTray.exe");
        Assert.Contains("/RL", args);
        Assert.Contains("HIGHEST", args);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains(@"C:\Apps\VPNTray.exe", string.Join(" ", args));
    }
}
