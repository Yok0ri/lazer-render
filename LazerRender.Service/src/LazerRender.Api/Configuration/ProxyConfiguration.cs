using System.Net;

namespace LazerRender.Api.Configuration;

/// <summary>
/// Trust rules for the reverse-proxy deployment.
///
/// The shipped topology terminates TLS at Cloudflare/NGinx/Caddy and speaks plain HTTP to
/// <c>127.0.0.1:5080</c>. Without forwarding support the app cannot tell HTTPS from HTTP — so cookies
/// are issued without <c>Secure</c> and the HTTPS redirect does nothing — and every request appears to
/// come from the proxy, collapsing the rate limiter into one shared bucket.
///
/// Only the listed addresses are trusted; a client connecting directly keeps its own address and
/// scheme, so it cannot spoof <c>X-Forwarded-For</c> to evade the limiter. This type is deliberately
/// free of ASP.NET Core types so the rules can be unit-tested without a test host; the mapping onto
/// <c>ForwardedHeadersOptions</c> lives in <c>Program.cs</c>.
/// </summary>
internal static class ProxyConfiguration
{
    /// <summary>Trusted when nothing is configured: the proxy talks to the app over loopback.</summary>
    public static readonly string[] DefaultKnownProxies = { "127.0.0.1", "::1" };

    public readonly record struct NetworkPrefix(IPAddress Prefix, int PrefixLength);

    public sealed record TrustList(IReadOnlyList<IPAddress> Proxies, IReadOnlyList<NetworkPrefix> Networks);

    /// <summary>
    /// Validates the configured trust list. Malformed configuration fails fast at startup rather than
    /// silently trusting nothing (degraded) or everything (spoofable).
    /// </summary>
    public static TrustList Parse(IEnumerable<string>? knownProxies, IEnumerable<string>? knownNetworks)
    {
        List<string> configuredProxies = DropBlanks(knownProxies);

        var proxies = new List<IPAddress>();

        // Trust the configured proxies, or loopback when none are configured.
        IEnumerable<string> trustedProxies = configuredProxies.Count > 0
            ? configuredProxies
            : DefaultKnownProxies;

        foreach (string proxy in trustedProxies)
        {
            if (!IPAddress.TryParse(proxy, out IPAddress? address))
                throw new InvalidOperationException($"Proxy:KnownProxies contains an invalid IP address: \"{proxy}\".");

            proxies.Add(address);
        }

        var networks = new List<NetworkPrefix>();

        foreach (string network in DropBlanks(knownNetworks))
        {
            string[] parts = network.Split('/', 2);

            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out IPAddress? prefix)
                || !int.TryParse(parts[1], out int prefixLength))
            {
                throw new InvalidOperationException(
                    $"Proxy:KnownNetworks contains an invalid network: \"{network}\". Expected CIDR, e.g. 172.18.0.0/16.");
            }

            networks.Add(new NetworkPrefix(prefix, prefixLength));
        }

        return new TrustList(proxies, networks);
    }

    private static List<string> DropBlanks(IEnumerable<string>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>();
}
