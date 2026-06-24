namespace VPNTray.Util;

public sealed class AppLogger
{
    private readonly string _dir;
    private readonly object _lock = new();

    public AppLogger(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");
    }

    public static AppLogger Shared { get; } = new();

    public string LogPath => Path.Combine(_dir, "vpntray.log");

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_lock) { File.AppendAllText(LogPath, line); }
        }
        catch { /* logging must never throw */ }
    }
}
