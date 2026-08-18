using System.Diagnostics;
using System.Net;
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
    private const string TempFilePrefix = "vpntray-";
    private const string TokenFileExtension = ".tok";
    private const string DnsReportExtension = ".dns";

    /// <summary>
    /// Names the file the --no-dns vpnc-script writes the gateway's resolvers to. Read by
    /// Resources/vpnc-script-win--no-dns.js under exactly this name; changing it in one place
    /// only turns internal-suffix redirection off without any other symptom.
    /// </summary>
    private const string DnsReportEnvVar = "VPNTRAY_DNS_REPORT";

    /// <summary>
    /// How long one <see cref="TunnelDnsServersAsync"/> call waits for that file.
    ///
    /// Sized generously because the handshake resolving says nothing about the script having
    /// run: openconnect prints "CSTP connected" from the CSTP setup, before it brings the tun
    /// device up and launches the script at all, and the analyzer will also call a connection
    /// good on liveness alone after its grace period. The script then shells out to route and
    /// netsh a dozen times before reaching the report, and `netsh interface ip set address`
    /// on its own can take a second. Only configs with internal-DNS suffixes ever wait.
    /// </summary>
    private static readonly TimeSpan DnsReportWait = TimeSpan.FromSeconds(5);

    /// <summary>A gateway pushes two or three. Bounds what a malformed file can turn into rules.</summary>
    private const int MaxReportedDnsServers = 8;

    private Process? _process;
    private string? _tokenFile;
    private string? _dnsReportFile;
    private IReadOnlyList<string> _tunnelDnsServers = Array.Empty<string>();
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();

    /// <summary>
    /// Cached once the report has been read; until then every call re-reads, so a suffix list
    /// added under a live connection still finds its resolvers. Written and read from different
    /// threads (connect flow, settings-save refresh).
    /// </summary>
    public async Task<IReadOnlyList<string>> TunnelDnsServersAsync()
    {
        var known = Volatile.Read(ref _tunnelDnsServers);
        return known.Count > 0 ? known : await CaptureTunnelDnsAsync();
    }

    public void Start(VpnConfig config, string password)
    {
        Stop(); // clean up any prior invocation (also deletes its temp files)
        SweepStaleTempFiles();
        lock (_lock) { _stderr.Clear(); }
        Volatile.Write(ref _tunnelDnsServers, Array.Empty<string>());

        // Interlocked, matching the delete side: StopForShutdown reaches Stop() without the
        // controller's gate, so it can null this field while Start is mid-flight. A plain write
        // losing that race orphans a file holding the TOTP secret until the next sweep.
        string tokenFile = WriteTokenSecret(config.TotpSecret);
        DeleteTempFile(ref _tokenFile);
        Interlocked.Exchange(ref _tokenFile, tokenFile);

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

        // Asked for whenever the tunnel is not taking DNS over, not only when a suffix list
        // exists today: the lists are editable under a live connection, and a list added mid
        // session would otherwise have no resolvers to point at until the next reconnect.
        // openconnect passes its own environment down to the script.
        if (config.SkipDnsModification)
        {
            string reportFile = TempFilePath(DnsReportExtension);
            DeleteTempFile(ref _dnsReportFile);
            Interlocked.Exchange(ref _dnsReportFile, reportFile);
            psi.Environment[DnsReportEnvVar] = reportFile;
        }

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
            DeleteDnsReport();
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
            // The DNS report is deliberately NOT deleted here. The script may not have written
            // it yet — the handshake resolving does not mean the script has run — and deleting
            // it would turn "not yet" into "never" for the rest of the session. Stop() owns it.
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
        try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { p?.Dispose(); } catch { /* ignore */ }
        // After the kill, not before: the script is a child of openconnect and may be between
        // creating the report and closing it, in which case an earlier delete just loses the
        // handle on a file the script goes on to re-create and nothing ever cleans up.
        DeleteDnsReport();
    }

    /// <summary>
    /// Park the Base32 TOTP secret in an owner-only temp file so it can be handed to
    /// openconnect via <c>--token-secret=@FILE</c> instead of the command line.
    /// </summary>
    private static string WriteTokenSecret(string totpSecret)
    {
        string path = TempFilePath(TokenFileExtension);
        File.WriteAllText(path, OpenConnectArgs.TokenSecretFileContent(totpSecret)); // no trailing newline
        FileAcl.RestrictToCurrentUser(path);
        return path;
    }

    private static string TempFilePath(string extension)
        => Path.Combine(Path.GetTempPath(), TempFilePrefix + Guid.NewGuid().ToString("N") + extension);

    /// <summary>Idempotent and safe to call from any thread (Start / handshake / Stop).</summary>
    private void DeleteTokenFile() => DeleteTempFile(ref _tokenFile);

    /// <summary>Idempotent and safe to call from any thread, same as the token file.</summary>
    private void DeleteDnsReport() => DeleteTempFile(ref _dnsReportFile);

    private static void DeleteTempFile(ref string? field)
    {
        var path = Interlocked.Exchange(ref field, null);
        if (path is null) return;
        try { File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>
    /// Reads the resolvers the --no-dns vpnc-script reported, when one was asked for. Polls
    /// rather than reading once, because the caller has no way to know whether the script has
    /// got that far yet. Returns empty when the wait runs out; the caller decides what that
    /// means, and may call again later.
    /// </summary>
    private async Task<IReadOnlyList<string>> CaptureTunnelDnsAsync()
    {
        var path = _dnsReportFile;
        if (path is null) return Array.Empty<string>();

        var servers = await ReadDnsReportAsync(path, DnsReportWait);
        if (servers.Count == 0) return servers;

        Volatile.Write(ref _tunnelDnsServers, servers);
        AppLogger.Shared.Info("tunnel DNS pushed by the gateway: " + string.Join(" ", servers));
        return servers;
    }

    /// <summary>
    /// Polls <paramref name="path"/> until it yields at least one usable resolver or
    /// <paramref name="budget"/> runs out, then returns what it found — empty if nothing.
    /// Separate from the caching around it so the timing can be tested without a tunnel.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadDnsReportAsync(string path, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    var servers = ParseDnsReport(File.ReadAllText(path));
                    if (servers.Count > 0) return servers;
                }
            }
            catch (IOException) { /* still being written; the retry covers it */ }
            catch (UnauthorizedAccessException) { /* same */ }

            if (DateTimeOffset.UtcNow >= deadline) return Array.Empty<string>();
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The report holds whatever the gateway put in INTERNAL_IP4_DNS: space separated, and it
    /// may carry IPv6 addresses despite the name. Anything that is not an address is dropped
    /// rather than passed on — these end up in a powershell command line.
    ///
    /// Public so the filtering can be tested directly, as with SplitDnsManager.BuildApplyCommand:
    /// it is the boundary where untrusted input stops being untrusted.
    /// </summary>
    public static IReadOnlyList<string> ParseDnsReport(string text)
        => text.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => IPAddress.TryParse(value, out var ip) && IsPlausibleTunnelResolver(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxReportedDnsServers)
            .ToList();

    /// <summary>
    /// A resolver reached through the tunnel is on a private network by definition. NRPT sets
    /// which server a name is asked of but says nothing about how the packet is routed, so a
    /// public address here would send internal hostnames out the physical NIC in the clear —
    /// to whoever owns that address. A gateway pushing one is either misconfigured or hostile,
    /// and 0.0.0.0 / loopback are neither routable nor a resolver.
    /// </summary>
    private static bool IsPlausibleTunnelResolver(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7

        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)                     // link-local
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);      // CGNAT, what this fleet's VPC uses
    }

    /// <summary>
    /// Best-effort cleanup of token and DNS-report files orphaned by a hard kill (Task Manager,
    /// crash), where neither Stop nor the handshake's finally block ever ran.
    /// </summary>
    private static void SweepStaleTempFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), TempFilePrefix + "*"))
            {
                if (!file.EndsWith(TokenFileExtension, StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(DnsReportExtension, StringComparison.OrdinalIgnoreCase)) continue;
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
