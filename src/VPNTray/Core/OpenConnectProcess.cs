using System.Diagnostics;
using System.Text;
using VPNTray.Config;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.Core;

public static class OpenConnectArgs
{
    /// <summary>
    /// Two-stage auth: the password arrives on stdin, and openconnect answers the
    /// separate OTP/challenge prompt itself from the TOTP secret in
    /// <paramref name="tokenSecretPath"/>. The secret is passed by file — never on the
    /// command line, which is readable by other processes of this user.
    /// </summary>
    public static IReadOnlyList<string> Build(VpnConfig config, string tokenSecretPath)
    {
        string script = config.SkipDnsModification
            ? BundledBinaries.VpncScriptNoDns
            : config.VpncScriptPath;

        return new List<string>
        {
            "--script", script,
            "--user", config.Username,
            "--passwd-on-stdin",                    // stage 1: the password
            "--token-mode", "totp",                 // stage 2: openconnect fills in the OTP
            "--token-secret", "@" + tokenSecretPath,
            "--non-inter",                          // fail loudly on an unexpected form instead of hanging
            "--servercert", config.ServerCertPin,
            config.Gateway,
        };
    }

    /// <summary>
    /// Body of the <c>--token-secret=@FILE</c> file. Normalizes the secret because
    /// openconnect's Base32 decoder rejects the grouping spaces and padding that people
    /// routinely paste ("Invalid base32 token string").
    /// </summary>
    public static string TokenSecretFileContent(string totpSecret)
        => "base32:" + TotpGenerator.NormalizeSecret(totpSecret);
}

public sealed class OpenConnectProcess : IOpenConnectProcessRunning
{
    private const string TokenFilePrefix = "vpntray-";
    private const string TokenFileExtension = ".tok";

    private Process? _process;
    private string? _tokenFile;
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();

    public void Start(VpnConfig config, string password)
    {
        Stop(); // clean up any prior invocation (also deletes its token file)
        SweepStaleTokenFiles();
        lock (_lock) { _stderr.Clear(); }

        string tokenFile = WriteTokenSecret(config.TotpSecret);
        _tokenFile = tokenFile;

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
        foreach (var arg in OpenConnectArgs.Build(config, tokenFile)) psi.ArgumentList.Add(arg);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_lock) { _stderr.AppendLine(e.Data); }
        };
        p.OutputDataReceived += (_, _) => { }; // discard stdout; BeginOutputReadLine keeps the pipe drained
        try
        {
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            // Do NOT log the password or the TOTP secret.
            AppLogger.Shared.Info("openconnect spawned");
            p.StandardInput.WriteLine(password);
            p.StandardInput.Close();
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.Dispose(); } catch { /* ignore */ }
            DeleteTokenFile();
            throw;
        }
        _process = p;
    }

    public async Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        // Two-stage auth (password, then OTP challenge) needs two round trips, so the
        // "alive past the grace period means connected" fallback must not fire early.
        var grace = TimeSpan.FromSeconds(8);

        try
        {
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
        finally
        {
            // openconnect reads --token-secret=@FILE while parsing arguments, so the
            // secret is in its memory well before the handshake resolves either way.
            DeleteTokenFile();
        }
    }

    public bool IsRunning()
    {
        var p = _process;
        try { return p is not null && !p.HasExited; }
        catch { return false; }
    }

    public void Stop()
    {
        DeleteTokenFile();
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Park the Base32 TOTP secret in an owner-only temp file so it can be handed to
    /// openconnect via <c>--token-secret=@FILE</c> instead of the command line.
    /// </summary>
    private static string WriteTokenSecret(string totpSecret)
    {
        string path = Path.Combine(
            Path.GetTempPath(), TokenFilePrefix + Guid.NewGuid().ToString("N") + TokenFileExtension);
        File.WriteAllText(path, OpenConnectArgs.TokenSecretFileContent(totpSecret)); // no trailing newline
        FileAcl.RestrictToCurrentUser(path);
        return path;
    }

    /// <summary>Idempotent and safe to call from any thread (Start / handshake / Stop).</summary>
    private void DeleteTokenFile()
    {
        var path = Interlocked.Exchange(ref _tokenFile, null);
        if (path is null) return;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>
    /// Best-effort cleanup of token files orphaned by a hard kill (Task Manager, crash),
    /// where neither Stop nor the handshake's finally block ever ran.
    /// </summary>
    private static void SweepStaleTokenFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(
                         Path.GetTempPath(), TokenFilePrefix + "*" + TokenFileExtension))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* ignore individual files */ }
            }
        }
        catch { /* ignore */ }
    }
}
