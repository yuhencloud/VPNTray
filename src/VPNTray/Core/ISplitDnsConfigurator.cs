namespace VPNTray.Core;

/// <summary>
/// Installs and removes the NRPT rules that route a list of suffixes to resolvers of their own,
/// whichever way the current DNS mode needs them to point. See <see cref="SplitDnsManager"/> for
/// why they are needed.
/// </summary>
public interface ISplitDnsConfigurator
{
    /// <summary>
    /// Replace this app's rules with one per entry in <paramref name="namespaces"/>, each
    /// resolving through <paramref name="servers"/>. Idempotent: any rule this app left
    /// behind earlier is removed first. Never throws.
    /// </summary>
    void Apply(IReadOnlyList<string> namespaces, IReadOnlyList<string> servers);

    /// <summary>Remove every rule this app installed. Idempotent. Never throws.</summary>
    void Cleanup();
}
