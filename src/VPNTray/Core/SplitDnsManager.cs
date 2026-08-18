using System.Net;
using System.Text.RegularExpressions;
using VPNTray.Util;

namespace VPNTray.Core;

/// <summary>
/// Split-DNS compensation for openconnect on Windows: an NRPT (Name Resolution Policy Table)
/// rule per suffix, sending that suffix to resolvers of its own while everything else follows
/// whatever the system would do anyway. AnyConnect works out of the box because it implements
/// this natively. This class does not care which direction it is pointing — see
/// <see cref="VpnController"/> for how each DNS mode fills in the two lists.
///
/// It exists because vpnc-script-win.js ignores the gateway's CISCO_SPLIT_DNS list entirely:
/// it pins the internal resolvers onto the tunnel adapter and, because that adapter's metric
/// is lower than Wi-Fi's, every name in the system ends up being asked of the internal DNS.
/// On a split tunnel that is actively wrong for public names — the internal resolver answers
/// them with in-cloud addresses (100.64.0.0/10, only routable inside the VPC), the default
/// route sends those packets out the physical NIC, and they black-hole. The --no-dns variant
/// of the script avoids that by leaving system DNS alone, which then needs rules in the other
/// direction for the internal names it no longer resolves.
///
/// Every rule is stamped with <see cref="RuleTag"/> so cleanup only ever removes rules this
/// app created — NRPT is machine-wide and may well hold rules put there by someone else.
/// </summary>
public sealed class SplitDnsManager : ISplitDnsConfigurator
{
    /// <summary>
    /// Deliberately distinct from the vpn_win.py launcher's own "vpn_win_split_dns" tag:
    /// whichever tool runs must not delete the other's rules.
    /// </summary>
    public const string RuleTag = "vpntray_split_dns";

    /// <summary>
    /// Generous, because this only bounds a hung powershell.exe — the cmdlets themselves
    /// finish in well under a second.
    /// </summary>
    private const int TimeoutSeconds = 20;

    private readonly IProcessRunner _runner;

    public SplitDnsManager(IProcessRunner runner) => _runner = runner;

    /// <summary>Removes only the rules stamped with <see cref="RuleTag"/>.</summary>
    public static string RemoveTaggedCommand =>
        $"Get-DnsClientNrptRule | Where-Object {{ $_.Comment -eq '{RuleTag}' }} | " +
        "ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }";

    public void Apply(IReadOnlyList<string> namespaces, IReadOnlyList<string> servers)
    {
        foreach (var bad in namespaces.Where(n => !IsValidNamespace(n)))
            AppLogger.Shared.Error($"split-DNS: ignoring malformed domain '{bad}'");
        foreach (var bad in servers.Where(s => !IsValidServer(s)))
            AppLogger.Shared.Error($"split-DNS: ignoring malformed DNS server '{bad}'");

        var command = BuildApplyCommand(namespaces, servers);
        if (command is null)
        {
            // Nothing left to install — but a previous run's rules may still be in place,
            // and leaving them would pin those domains to public DNS forever.
            Cleanup();
            return;
        }

        var result = RunPowerShell(command);
        ReportApplyResult(result, servers);
    }

    public void Cleanup()
    {
        var result = RunPowerShell(RemoveTaggedCommand);
        if (result.Succeeded)
            AppLogger.Shared.Info("split-DNS: rules removed");
        else
            AppLogger.Shared.Error(
                "split-DNS: cleanup failed (check `Get-DnsClientNrptRule` manually): "
                + Combined(result).Trim());
    }

    /// <summary>
    /// Builds "remove leftovers, add each rule, flush the cache" as ONE powershell command.
    /// Each spawn costs 0.3-0.6s, and doing this in three spawns showed up as a visible
    /// stall on every reconnect. Per-domain try/catch keeps the failure granularity.
    /// Returns null when there is nothing valid to install.
    /// </summary>
    public static string? BuildApplyCommand(IReadOnlyList<string> namespaces, IReadOnlyList<string> servers)
    {
        var validNamespaces = namespaces.Where(IsValidNamespace).ToList();
        // Re-emit through IPAddress so only a canonical literal can ever reach the command line.
        var validServers = servers.Where(IsValidServer).Select(s => IPAddress.Parse(s).ToString()).ToList();
        if (validNamespaces.Count == 0 || validServers.Count == 0) return null;

        string serverList = string.Join(",", validServers.Select(s => $"'{s}'"));

        // Removing first also covers the case where the app was killed outright (Task
        // Manager, crash) and never got to run its own cleanup.
        var steps = new List<string> { RemoveTaggedCommand };
        steps.AddRange(validNamespaces.Select(ns =>
            $"try {{ Add-DnsClientNrptRule -Namespace '{ns}' -NameServers {serverList} " +
            $"-Comment '{RuleTag}' -DisplayName '{RuleTag}' -ErrorAction Stop; Write-Output 'OK {ns}' }} " +
            $"catch {{ Write-Output ('FAIL {ns} -> ' + $_.Exception.Message) }}"));
        steps.Add("Clear-DnsClientCache");

        return string.Join("; ", steps);
    }

    /// <summary>
    /// A DNS suffix as NRPT wants it: a leading dot means "this suffix and everything under
    /// it". Strict on purpose — these strings are interpolated into a powershell command, so
    /// anything outside the label alphabet is rejected rather than escaped.
    /// </summary>
    public static bool IsValidNamespace(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 253 && NamespacePattern.IsMatch(value);

    /// <summary>
    /// True for a suffix that captures a whole top-level domain: a leading dot and one label,
    /// like ".com". Syntactically perfect, and as an internal-DNS entry it would route every
    /// name under that TLD into the tunnel for every account on the machine.
    ///
    /// Not an error — ".internal", ".corp" and ".lan" have exactly this shape and are the
    /// private TLDs people legitimately run — which is why this reports rather than rejects.
    /// A dotless entry ("intranet") is an exact name, not a wildcard, so it does not count.
    /// </summary>
    public static bool CoversAnEntireTld(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith('.') && !value.TrimStart('.').Contains('.');

    public static bool IsValidServer(string? value)
        => !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out _);

    /// <remarks>
    /// Anchored with \z rather than $, which in .NET also matches before a trailing newline —
    /// this predicate is the last gate before a powershell command line, so it must not depend
    /// on the caller having stripped control characters first.
    /// </remarks>
    private static readonly Regex NamespacePattern = new(
        @"^\.?[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]*[A-Za-z0-9])?)*\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private ProcessResult RunPowerShell(string command)
    {
        try
        {
            return _runner.Run(
                "powershell",
                new[] { "-NoProfile", "-NonInteractive", "-Command", command },
                TimeoutSeconds);
        }
        catch (Exception ex)
        {
            // Split-DNS is a best-effort refinement; failing to configure it must never take
            // down an otherwise working tunnel.
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    private static void ReportApplyResult(ProcessResult result, IReadOnlyList<string> servers)
    {
        string via = string.Join(" ", servers.Where(IsValidServer));
        int reported = 0;

        foreach (var raw in Combined(result).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("OK ", StringComparison.Ordinal))
            {
                reported++;
                AppLogger.Shared.Info($"split-DNS: {line[3..]} -> {via}");
            }
            else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
            {
                reported++;
                AppLogger.Shared.Error($"split-DNS: rule failed: {line[5..]}");
            }
        }

        // Not one OK/FAIL came back, so the script never ran at all: powershell missing,
        // timed out, or blocked by execution policy.
        if (reported == 0)
            AppLogger.Shared.Error(
                $"split-DNS: no rules applied (powershell exit {result.ExitCode}): {Combined(result).Trim()}");
    }

    private static string Combined(ProcessResult result) => result.Stdout + result.Stderr;
}
