using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Dependencies;

public sealed record DependencyStatus(string Name, bool Passed, string? FixHint);

public sealed class DependencyChecker
{
    private readonly IProcessRunner _runner;

    public DependencyChecker(IProcessRunner runner) => _runner = runner;

    public IReadOnlyList<DependencyStatus> Check(VpnConfig config)
    {
        var results = new List<DependencyStatus>();

        bool exeExists = File.Exists(config.OpenconnectPath);
        results.Add(new DependencyStatus(
            "openconnect.exe present", exeExists,
            exeExists ? null : "Bundled openconnect.exe is missing — reinstall VPNTray."));

        bool versionOk = false;
        if (exeExists)
        {
            try
            {
                var r = _runner.Run(config.OpenconnectPath, new[] { "--version" }, timeoutSeconds: 5);
                versionOk = r.Succeeded;
            }
            catch { versionOk = false; }
        }
        results.Add(new DependencyStatus(
            "openconnect runs", versionOk,
            versionOk ? null : "openconnect.exe failed to run — the bundle may be corrupt or blocked by SmartScreen."));

        bool wintunOk = File.Exists(BundledBinaries.WintunDll);
        results.Add(new DependencyStatus(
            "wintun.dll present", wintunOk,
            wintunOk ? null : "wintun.dll is missing next to openconnect.exe — reinstall VPNTray."));

        string script = config.SkipDnsModification ? BundledBinaries.VpncScriptNoDns : config.VpncScriptPath;
        bool scriptOk = File.Exists(script);
        results.Add(new DependencyStatus(
            "vpnc script present", scriptOk,
            scriptOk ? null : $"VPN config script not found at {script}."));

        return results;
    }
}
