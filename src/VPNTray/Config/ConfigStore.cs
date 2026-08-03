using System.Text.Json;
using VPNTray.Util;

namespace VPNTray.Config;

public class ConfigStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ConfigStore(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");
    }

    public string FilePath => Path.Combine(_dir, "config.json");

    public VpnConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<VpnConfig>(json, Options);
        }
        catch (Exception)
        {
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    public void Save(VpnConfig config)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        FileAcl.RestrictToCurrentUser(tmp);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
