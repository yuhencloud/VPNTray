using VPNTray.Core;

namespace VPNTray.Tests.Core;

internal sealed class RecordingRunner : IProcessRunner
{
    public readonly List<(string Exe, IReadOnlyList<string> Args)> Calls = new();
    public ProcessResult Result = new(0, "", "");
    public Exception? ThrowOnRun;

    public IReadOnlyList<string> LastCommand =>
        Calls.Count == 0 ? Array.Empty<string>() : Calls[^1].Args;

    public string LastCommandText => LastCommand.Count == 0 ? "" : LastCommand[^1];

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds)
    {
        Calls.Add((executable, arguments));
        if (ThrowOnRun is not null) throw ThrowOnRun;
        return Result;
    }
}

public class SplitDnsManagerTests
{
    private static readonly string[] Namespaces = { ".example.com" };
    private static readonly string[] Servers = { "8.8.8.8", "1.1.1.1" };

    [Fact]
    public void BuildApplyCommand_RemovesLeftoversFirst_ThenAddsRules_ThenFlushesCache()
    {
        var command = SplitDnsManager.BuildApplyCommand(Namespaces, Servers)!;

        Assert.StartsWith(SplitDnsManager.RemoveTaggedCommand, command);
        Assert.Contains("Add-DnsClientNrptRule -Namespace '.example.com'", command);
        Assert.Contains("-NameServers '8.8.8.8','1.1.1.1'", command);
        Assert.EndsWith("Clear-DnsClientCache", command);
    }

    [Fact]
    public void BuildApplyCommand_StampsEveryRuleWithTheAppTag()
    {
        var command = SplitDnsManager.BuildApplyCommand(new[] { ".a.com", ".b.com" }, Servers)!;
        Assert.Equal(2, Occurrences(command, $"-Comment '{SplitDnsManager.RuleTag}'"));
    }

    [Fact]
    public void RemoveTaggedCommand_FiltersOnTheAppTag_SoForeignRulesSurvive()
    {
        Assert.Contains($"$_.Comment -eq '{SplitDnsManager.RuleTag}'", SplitDnsManager.RemoveTaggedCommand);
        // The python launcher writes its own tag; deleting its rules would break it mid-run.
        Assert.NotEqual("vpn_win_split_dns", SplitDnsManager.RuleTag);
    }

    [Fact]
    public void BuildApplyCommand_ReturnsNull_WhenNothingValidIsLeft()
    {
        Assert.Null(SplitDnsManager.BuildApplyCommand(Array.Empty<string>(), Servers));
        Assert.Null(SplitDnsManager.BuildApplyCommand(Namespaces, Array.Empty<string>()));
        Assert.Null(SplitDnsManager.BuildApplyCommand(new[] { "not a domain" }, Servers));
        Assert.Null(SplitDnsManager.BuildApplyCommand(Namespaces, new[] { "nope" }));
    }

    [Fact]
    public void BuildApplyCommand_DropsMalformedEntries_ButKeepsTheGoodOnes()
    {
        var command = SplitDnsManager.BuildApplyCommand(
            new[] { ".good.com", "'; Remove-Item C:\\ -Recurse; '" }, Servers)!;

        Assert.Contains(".good.com", command);
        Assert.DoesNotContain("Remove-Item", command);
    }

    [Theory]
    [InlineData(".example.com")]
    [InlineData("api.example.com")]
    [InlineData("example")]
    public void IsValidNamespace_AcceptsSuffixesAndHostnames(string value)
        => Assert.True(SplitDnsManager.IsValidNamespace(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("has space.com")]
    [InlineData("quote'.com")]
    [InlineData("semi;colon.com")]
    [InlineData("-leadingdash.com")]
    [InlineData("double..dot.com")]
    public void IsValidNamespace_RejectsAnythingOutsideTheLabelAlphabet(string? value)
        => Assert.False(SplitDnsManager.IsValidNamespace(value));

    [Fact]
    public void Apply_InvokesPowerShellNonInteractively()
    {
        var runner = new RecordingRunner();
        new SplitDnsManager(runner).Apply(Namespaces, Servers);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("powershell", call.Exe);
        Assert.Contains("-NoProfile", call.Args);
        Assert.Contains("-NonInteractive", call.Args);
        Assert.Contains("Add-DnsClientNrptRule", runner.LastCommandText);
    }

    [Fact]
    public void Apply_WithNothingToInstall_StillClearsRulesFromAnEarlierRun()
    {
        var runner = new RecordingRunner();
        new SplitDnsManager(runner).Apply(Array.Empty<string>(), Servers);

        Assert.Single(runner.Calls);
        Assert.Equal(SplitDnsManager.RemoveTaggedCommand, runner.LastCommandText);
    }

    [Fact]
    public void Cleanup_RemovesOnlyTaggedRules()
    {
        var runner = new RecordingRunner();
        new SplitDnsManager(runner).Cleanup();

        Assert.Equal(SplitDnsManager.RemoveTaggedCommand, runner.LastCommandText);
    }

    [Fact]
    public void Apply_SwallowsRunnerFailures_SoASplitDnsProblemCannotDropTheTunnel()
    {
        var throwing = new RecordingRunner { ThrowOnRun = new InvalidOperationException("powershell missing") };
        var manager = new SplitDnsManager(throwing);

        manager.Apply(Namespaces, Servers);   // must not throw
        manager.Cleanup();                    // must not throw
    }

    [Fact]
    public void Apply_ToleratesANonZeroExit()
    {
        var runner = new RecordingRunner { Result = new ProcessResult(1, "", "denied") };
        new SplitDnsManager(runner).Apply(Namespaces, Servers);
        Assert.Single(runner.Calls);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
