using System.Diagnostics;
using Microsoft.Win32;

namespace VPNTray.Util;

public sealed class StartupManager
{
    private const string TaskName = "VPNTray";
    private const string LaunchValue = "LaunchAtLogin";
    private const string ConnectValue = "ConnectAtLogin";

    private readonly string _registryKey;

    /// <summary>Key override exists for tests, which must not touch the real Software\VPNTray.</summary>
    public StartupManager(string registryKey = @"Software\VPNTray") => _registryKey = registryKey;

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
        using var key = Registry.CurrentUser.OpenSubKey(_registryKey);
        var value = key?.GetValue(LaunchValue) as int?;
        // Default-on: absent value means enabled (mirrors mac LoginItemManager).
        return value is null || value != 0;
    }

    /// <summary>
    /// Whether the logon start should also dial the VPN. Default-OFF, unlike launch-at-login:
    /// connecting a corporate VPN at every sign-in is not something to assume on anyone's behalf.
    /// </summary>
    public bool IsConnectAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registryKey);
        return key?.GetValue(ConnectValue) as int? is int v && v != 0;
    }

    /// <summary>Pure registry write — no scheduled task involved, unlike Enable/Disable.</summary>
    public void SetConnectAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryKey);
        key.SetValue(ConnectValue, enabled ? 1 : 0, RegistryValueKind.DWord);
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
        using var key = Registry.CurrentUser.CreateSubKey(_registryKey);
        key.SetValue(LaunchValue, enabled ? 1 : 0, RegistryValueKind.DWord);
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
