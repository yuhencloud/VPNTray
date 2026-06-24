using System.Diagnostics;
using System.Text;
using VPNTray.Config;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.Core;

public static class OpenConnectArgs
{
    public static IReadOnlyList<string> Build(VpnConfig config)
    {
        string script = config.SkipDnsModification
            ? BundledBinaries.VpncScriptNoDns
            : config.VpncScriptPath;

        return new List<string>
        {
            "--script", script,
            "--user", config.Username,
            "--passwd-on-stdin",
            "--servercert", config.ServerCertPin,
            config.Gateway,
        };
    }
}

public sealed class OpenConnectProcess : IOpenConnectProcessRunning
{
    private Process? _process;
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();

    public void Start(VpnConfig config, string password)
    {
        Stop(); // clean up any prior invocation
        lock (_lock) { _stderr.Clear(); }

        var psi = new ProcessStartInfo
        {
            FileName = config.OpenconnectPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = BundledBinaries.BaseDir, // so wintun.dll resolves
        };
        foreach (var arg in OpenConnectArgs.Build(config)) psi.ArgumentList.Add(arg);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_lock) { _stderr.AppendLine(e.Data); }
        };
        p.OutputDataReceived += (_, _) => { }; // discard stdout; BeginOutputReadLine keeps the pipe drained
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        // Do NOT log the password.
        AppLogger.Shared.Info("openconnect spawned");
        try
        {
            p.StandardInput.WriteLine(password);
            p.StandardInput.Close();
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.Dispose(); } catch { /* ignore */ }
            throw;
        }
        _process = p;
    }

    public async Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(1.5);

        while (DateTimeOffset.UtcNow - start < timeout)
        {
            var p = _process;
            if (p is null) return new OpenConnectHandshake(false, "openconnect process not started");

            string stderr;
            lock (_lock) { stderr = _stderr.ToString(); }

            bool gracePassed = DateTimeOffset.UtcNow - start >= grace;
            var decided = HandshakeAnalyzer.Evaluate(
                stderr, processAlive: !p.HasExited, gracePassed: gracePassed, childConfirmedAlive: IsRunning());
            if (decided is not null)
            {
                if (!decided.Connected)
                    AppLogger.Shared.Error("openconnect handshake failed. Full stderr:\n" + stderr.Trim());
                return decided;
            }
            await Task.Delay(250);
        }

        string finalErr;
        lock (_lock) { finalErr = _stderr.ToString(); }
        AppLogger.Shared.Error("openconnect handshake timeout. Full stderr:\n" + finalErr.Trim());
        return new OpenConnectHandshake(false, HandshakeAnalyzer.FriendlyReason(finalErr));
    }

    public bool IsRunning()
    {
        var p = _process;
        try { return p is not null && !p.HasExited; }
        catch { return false; }
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }
}
