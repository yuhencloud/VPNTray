using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;

namespace VPNTray.Tests.Dependencies;

internal sealed class FakeRunner : IProcessRunner
{
    public int ExitToReturn = 0;
    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds)
        => new(ExitToReturn, "OpenConnect version v9.12", "");
}

public class DependencyCheckerTests
{
    [Fact]
    public void Check_ReturnsOneStatusPerDependency()
    {
        var checker = new DependencyChecker(new FakeRunner());
        var statuses = checker.Check(new VpnConfig());
        Assert.Equal(4, statuses.Count);
        Assert.Contains(statuses, s => s.Name.Contains("openconnect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_VersionProbeFails_WhenRunnerReturnsNonZero()
    {
        var exe = Path.Combine(Path.GetTempPath(), "vpntray-oc-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "");
        try
        {
            var config = new VpnConfig { OpenconnectPath = exe };
            var statuses = new DependencyChecker(new FakeRunner { ExitToReturn = 1 }).Check(config);
            var versionRow = statuses.First(s => s.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
            Assert.False(versionRow.Passed);
            Assert.NotNull(versionRow.FixHint);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void Check_VersionProbePasses_WhenExeExistsAndRunnerSucceeds()
    {
        var exe = Path.Combine(Path.GetTempPath(), "vpntray-oc-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "");
        try
        {
            var config = new VpnConfig { OpenconnectPath = exe };
            var statuses = new DependencyChecker(new FakeRunner { ExitToReturn = 0 }).Check(config);
            var versionRow = statuses.First(s => s.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
            Assert.True(versionRow.Passed);
        }
        finally
        {
            File.Delete(exe);
        }
    }
}
