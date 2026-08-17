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

    /// <summary>
    /// The login start is the one launch that should stay quiet. Every other route into the app
    /// — the installer's "Launch VPNTray", the Start menu, the exe itself — is somebody asking
    /// for it, and a process that only adds a tray icon reads as nothing having happened. Only
    /// the scheduled task's command line is ours to mark, so the flag goes on the quiet path.
    /// </summary>
    [Fact]
    public void BuildCreateArgs_MarksTheLoginStartAsBackground()
    {
        var args = StartupManager.BuildCreateArgs("VPNTray", @"C:\Apps\VPNTray.exe");

        var run = args[args.ToList().IndexOf("/TR") + 1];
        Assert.Equal($"\"C:\\Apps\\VPNTray.exe\" {StartupManager.BackgroundFlag}", run);
    }
}
