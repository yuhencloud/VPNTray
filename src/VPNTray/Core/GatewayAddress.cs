using System.Net;
using System.Net.Sockets;

namespace VPNTray.Core;

/// <summary>
/// Helpers for steering openconnect at one specific address of a gateway that has several.
///
/// Exists because a gateway name can resolve to more than one node, and one node can be
/// broken in a way DNS knows nothing about — accepting TCP but dropping the TLS handshake.
/// openconnect has no fallback for that: it gives up on the first address it reached. Pinning
/// the name with <c>--resolve HOST:IP</c> lets the app try the other nodes itself.
/// </summary>
public static class GatewayAddress
{
    /// <summary>Bounds how many nodes one connect attempt walks through.</summary>
    public const int MaxFallbackAddresses = 4;

    /// <summary>
    /// The host name inside a configured gateway, which may be a bare host, host:port, or a
    /// URL with a path. Null when there is no host, or when it is already an IP literal —
    /// there is nothing to resolve differently then.
    /// </summary>
    public static string? HostName(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return null;
        string candidate = gateway.Contains("://", StringComparison.Ordinal) ? gateway : "https://" + gateway;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic)) return null;
        return uri.Host.Length == 0 ? null : uri.Host;
    }

    /// <summary>
    /// The <c>--resolve</c> value. Rejects anything that is not an IP literal: it lands on a
    /// command line, and the addresses come from DNS, which is not trusted input.
    /// </summary>
    public static string ResolveArgument(string host, string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            throw new ArgumentException("Not an IP address.", nameof(address));
        return host + ":" + ip;
    }

    /// <summary>Every IPv4/IPv6 address <paramref name="host"/> resolves to, de-duplicated, in DNS order.</summary>
    public static async Task<IReadOnlyList<string>> ResolveAllAsync(string host)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(a => a.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
