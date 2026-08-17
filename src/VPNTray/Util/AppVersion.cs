using System.Reflection;

namespace VPNTray.Util;

/// <summary>
/// The running build's own version, for the title bar. Read once — it cannot change while the
/// process lives, and the reflection is not free.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;

        // Informational version carries what <Version> was set to, including any prerelease
        // suffix; AssemblyVersion drops that, so prefer this one.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return Trim(informational);

        return assembly.GetName().Version?.ToString(3) ?? "";
    }

    /// <summary>
    /// Drops the "+&lt;commit&gt;" build metadata the SDK appends whenever SourceRevisionId is
    /// set. Keeps any prerelease suffix, which is part of the version proper.
    /// </summary>
    public static string Trim(string version)
    {
        int plus = version.IndexOf('+');
        return (plus < 0 ? version : version[..plus]).Trim();
    }
}
