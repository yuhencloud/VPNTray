namespace VPNTray.Core;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds);
}
