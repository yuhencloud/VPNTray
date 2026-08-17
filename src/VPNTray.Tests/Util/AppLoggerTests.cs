using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class AppLoggerTests
{
    [Fact]
    public void Info_WritesLineToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpntray-log-" + Guid.NewGuid().ToString("N"));
        var logger = new AppLogger(dir);
        logger.Info("hello world");
        Assert.Contains("hello world", File.ReadAllText(logger.LogPath));
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Production code logs through <see cref="AppLogger.Shared"/>, whose default directory
    /// is the real %APPDATA%\VPNTray. A test run must not append its deliberately-fake
    /// failures there: that log is what someone reads when diagnosing a real problem.
    /// See TestLogRedirect for the redirect this asserts.
    /// </summary>
    [Fact]
    public void Shared_DuringATestRun_StaysOutOfTheRealAppDataLog()
    {
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");

        Assert.DoesNotContain(real, AppLogger.Shared.LogPath, StringComparison.OrdinalIgnoreCase);
    }
}
