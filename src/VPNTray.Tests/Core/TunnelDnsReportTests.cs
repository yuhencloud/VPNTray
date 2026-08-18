using VPNTray.Core;

namespace VPNTray.Tests.Core;

/// <summary>
/// The contract between <see cref="OpenConnectProcess"/> and the shipped vpnc-script variant.
/// It is split across a C# constant and a JScript file that gets re-derived by hand from
/// upstream every time openconnect is upgraded, and a mismatch has no symptom other than
/// internal-DNS domains quietly resolving through public DNS.
/// </summary>
public class TunnelDnsReportTests
{
    private static string NoDnsScript()
    {
        // Walk up from the test binary to the repo, rather than relying on the copy in the
        // output folder: the point is to check the file that actually ships.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "VPNTray", "Resources")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "VPNTray", "Resources", "vpnc-script-win--no-dns.js");
    }

    [Fact]
    public void TheShippedScript_StillReadsTheEnvironmentVariableTheAppSets()
    {
        var script = File.ReadAllText(NoDnsScript());

        Assert.Contains("VPNTRAY_DNS_REPORT", script, StringComparison.Ordinal);
        // Reporting the resolvers is the whole point; a copy that only disables the DNS block
        // would pass the check above via the comment alone.
        Assert.Contains("INTERNAL_IP4_DNS", script, StringComparison.Ordinal);
        Assert.Contains("CreateTextFile", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The DNS-add block must stay disabled. Re-deriving this file from a new upstream script
    /// and keeping only the reporting half would hand the tunnel the system's DNS again —
    /// exactly the breakage public-DNS mode exists to avoid, with no error anywhere.
    /// </summary>
    [Fact]
    public void TheShippedScript_StillHasItsDnsBlockDisabled()
    {
        var script = File.ReadAllText(NoDnsScript());

        Assert.Contains("if (false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("if (env(\"INTERNAL_IP4_DNS\"))", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDnsReport_TakesTheGatewaysResolvers_InOrder()
    {
        var servers = OpenConnectProcess.ParseDnsReport("10.0.0.53 10.0.0.54\r\n");

        Assert.Equal(new[] { "10.0.0.53", "10.0.0.54" }, servers);
    }

    /// <summary>
    /// These strings are interpolated into a powershell command line. Nothing that is not an
    /// address may survive the parser, whatever wrote the file.
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4';Start-Process calc;#")]
    [InlineData("not-an-address")]
    [InlineData("$(whoami)")]
    [InlineData("")]
    public void ParseDnsReport_DropsAnythingThatIsNotAnAddress(string content)
        => Assert.Empty(OpenConnectProcess.ParseDnsReport(content));

    /// <summary>
    /// NRPT names the resolver but says nothing about routing, so a public address here would
    /// send internal hostnames out the physical NIC in the clear, to whoever owns it.
    /// </summary>
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public void ParseDnsReport_DropsResolversTheTunnelCannotPlausiblyReach(string address)
        => Assert.Empty(OpenConnectProcess.ParseDnsReport(address));

    [Theory]
    [InlineData("10.0.0.53")]
    [InlineData("172.16.0.53")]
    [InlineData("192.168.1.53")]
    [InlineData("100.64.0.53")]   // CGNAT — what this fleet's VPC hands out
    [InlineData("fd00::53")]
    public void ParseDnsReport_KeepsPrivateResolvers(string address)
        => Assert.Equal(new[] { address }, OpenConnectProcess.ParseDnsReport(address));

    [Fact]
    public void ParseDnsReport_DropsDuplicates_AndBoundsTheCount()
    {
        var many = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"10.0.0.{i}"));

        Assert.Equal(8, OpenConnectProcess.ParseDnsReport(many).Count);
        Assert.Equal(new[] { "10.0.0.53" }, OpenConnectProcess.ParseDnsReport("10.0.0.53 10.0.0.53"));
    }

    /// <summary>
    /// The reason the read polls at all: the handshake resolving says nothing about the
    /// vpnc-script having reached its report, so the file routinely does not exist yet when
    /// the first read happens.
    /// </summary>
    [Fact]
    public async Task ReadDnsReport_WaitsForAFileThatDoesNotExistYet()
    {
        var path = Path.Combine(Path.GetTempPath(), "vpntray-test-" + Guid.NewGuid().ToString("N") + ".dns");
        try
        {
            var writer = Task.Run(async () =>
            {
                await Task.Delay(300);
                File.WriteAllText(path, "10.0.0.53 10.0.0.54\r\n");
            });

            var servers = await OpenConnectProcess.ReadDnsReportAsync(path, TimeSpan.FromSeconds(5));
            await writer;

            Assert.Equal(new[] { "10.0.0.53", "10.0.0.54" }, servers);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Giving up has to be an ordinary empty answer, not an exception or a hang: the tunnel is
    /// up regardless, and the caller may ask again later.
    /// </summary>
    [Fact]
    public async Task ReadDnsReport_ReturnsEmpty_WhenTheFileNeverArrives()
    {
        var path = Path.Combine(Path.GetTempPath(), "vpntray-test-" + Guid.NewGuid().ToString("N") + ".dns");

        var servers = await OpenConnectProcess.ReadDnsReportAsync(path, TimeSpan.FromMilliseconds(250));

        Assert.Empty(servers);
    }

    /// <summary>
    /// A file that exists but holds nothing usable is the same as no file: the script creates it
    /// before writing, and a gateway can push resolvers the tunnel could never reach.
    /// </summary>
    [Fact]
    public async Task ReadDnsReport_KeepsWaiting_WhileTheFileHoldsNothingUsable()
    {
        var path = Path.Combine(Path.GetTempPath(), "vpntray-test-" + Guid.NewGuid().ToString("N") + ".dns");
        try
        {
            File.WriteAllText(path, "8.8.8.8\r\n");   // public: rejected, so not an answer

            var servers = await OpenConnectProcess.ReadDnsReportAsync(path, TimeSpan.FromMilliseconds(250));

            Assert.Empty(servers);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
