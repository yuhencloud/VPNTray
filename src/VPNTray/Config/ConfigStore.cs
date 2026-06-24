using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

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
        RestrictToCurrentUser(tmp);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Windows equivalent of chmod 0600 — owner full control, no inheritance.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = new FileSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception)
        {
            // ACL hardening is best-effort; never block a save on it.
        }
    }
}
