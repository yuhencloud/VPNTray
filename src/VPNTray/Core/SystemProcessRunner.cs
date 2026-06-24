using System.Diagnostics;

namespace VPNTray.Core;

public sealed class SystemProcessRunner : IProcessRunner
{
    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ProcessResult(-1, "", "timed out");
        }
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}
