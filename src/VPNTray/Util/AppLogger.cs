namespace VPNTray.Util;

public sealed class AppLogger
{
    private readonly string _dir;
    private readonly object _lock = new();

    /// <summary>
    /// Process-wide override for the log directory. Exists so a test run cannot append its
    /// deliberately-fake failures to the real %APPDATA% log — that file is what someone
    /// reads when diagnosing an actual problem, and fake "powershell missing" lines in it
    /// send them down the wrong path.
    /// </summary>
    public const string LogDirVariable = "VPNTRAY_LOG_DIR";

    public AppLogger(string? directory = null)
    {
        _dir = directory
            ?? NonEmpty(Environment.GetEnvironmentVariable(LogDirVariable))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
