using System.Diagnostics;
using Microsoft.Win32;

namespace VPNTray.Util;

public sealed class StartupManager
{
    private const string TaskName = "VPNTray";
    private const string RegistryKey = @"Software\VPNTray";
    private const string RegistryValue = "LaunchAtLogin";

    /// <summary>
    /// Passed only by the logon task, so the app can tell "Windows started me" from "somebody
    /// asked for me" and keep quiet in the first case. The installer's launch, the Start menu
    /// shortcut and the exe itself all arrive without it and get a window.
    /// </summary>
    public const string BackgroundFlag = "--background";

    public static IReadOnlyList<string> BuildCreateArgs(string taskName, string exePath) => new[]
    {
        "/Create", "/F",
        "/TN", taskName,
        "/SC", "ONLOGON",
        "/RL", "HIGHEST",
        "/TR", $"\"{exePath}\" {BackgroundFlag}",
    };

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        var value = key?.GetValue(RegistryValue) as int?;
        // Default-on: absent value means enabled (mirrors mac LoginItemManager).
        return value is null || value != 0;
    }

    public void Enable()
    {
        var exe = Process.GetCurrentProcess().MainModule!.FileName;
        RunSchtasks(BuildCreateArgs(TaskName, exe));
        SetPreference(true);
    }

    public void Disable()
    {
        RunSchtasks(new[] { "/Delete", "/F", "/TN", TaskName });
        SetPreference(false);
    }

    /// <summary>Sync the registered task to the stored preference. Called once at launch.</summary>
    public void ApplyPreference()
    {
        if (IsEnabled()) Enable(); else Disable();
    }

    private void SetPreference(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        key.SetValue(RegistryValue, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void RunSchtasks(IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            AppLogger.Shared.Error("schtasks failed: " + ex.Message);
        }
    }
}
