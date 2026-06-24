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
        var checker = new DependencyChecker(new FakeRunner { ExitToReturn = 1 });
        var statuses = checker.Check(new VpnConfig());
        var versionRow = statuses.First(s => s.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
        Assert.False(versionRow.Passed);
        Assert.NotNull(versionRow.FixHint);
    }
}
