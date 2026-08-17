using System.Runtime.CompilerServices;
using VPNTray.Util;

namespace VPNTray.Tests;

/// <summary>
/// Points <see cref="AppLogger.Shared"/> at a scratch directory for the whole test run.
///
/// Production code logs through the shared instance, and several suites drive it with
/// deliberately-broken fakes — a run used to append lines like "split-DNS: no rules applied
/// (powershell exit -1): powershell missing" to the real %APPDATA%\VPNTray\vpntray.log,
/// which is the file someone reads when a real connection misbehaves.
///
/// A module initializer because <see cref="AppLogger.Shared"/> resolves its directory once,
/// on first touch; setting the variable from a fixture would be a race against whichever
/// test got there first.
/// </summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
        => Environment.SetEnvironmentVariable(
            AppLogger.LogDirVariable,
            Path.Combine(Path.GetTempPath(), "VPNTray.Tests", "logs"));
}
