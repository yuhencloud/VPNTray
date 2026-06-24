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
}
