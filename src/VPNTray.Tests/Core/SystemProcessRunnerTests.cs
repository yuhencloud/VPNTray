using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class SystemProcessRunnerTests
{
    [Fact]
    public void Run_CapturesStdout_AndZeroExit()
    {
        var runner = new SystemProcessRunner();
        var result = runner.Run("cmd.exe", new[] { "/c", "echo", "hello" }, timeoutSeconds: 5);
        Assert.True(result.Succeeded);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void Run_NonZeroExit_NotSucceeded()
    {
        var runner = new SystemProcessRunner();
        var result = runner.Run("cmd.exe", new[] { "/c", "exit", "3" }, timeoutSeconds: 5);
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
    }
}
