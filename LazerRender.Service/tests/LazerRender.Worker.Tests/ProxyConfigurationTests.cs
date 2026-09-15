using System.Net;
using LazerRender.Api.Configuration;
using Xunit;

namespace LazerRender.Worker.Tests;

/// <summary>
/// H-2 (audit): no forwarded-headers handling meant the app could not tell HTTPS from HTTP behind the
/// TLS-terminating proxy (cookies were issued without <c>Secure</c>, the HTTPS redirect was inert) and
/// saw every request as coming from the proxy, so the rate limiter collapsed into a single bucket.
///
/// These pin the trust rules: only configured proxies may assert a client address or scheme.
/// <c>Program.cs</c> clears the framework's own defaults (loopback plus a private range) before
/// applying them. The end-to-end assertion the audit asks for — that <c>Set-Cookie</c> carries
/// <c>Secure</c> and the limiter partitions per forwarded address — needs a test host and is recorded
/// as a manual verification step in DEPLOYMENT.md.
/// </summary>
public sealed class ProxyConfigurationTests
{
    [Fact]
    public void Nothing_configured_trusts_loopback_only()
    {
        ProxyConfiguration.TrustList trust = ProxyConfiguration.Parse(null, null);

        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, trust.Proxies);
        Assert.Empty(trust.Networks);
    }

    [Fact]
    public void Configured_proxies_replace_the_defaults()
    {
        ProxyConfiguration.TrustList trust = ProxyConfiguration.Parse(new[] { "10.0.0.5" }, null);

        Assert.Equal(new[] { IPAddress.Parse("10.0.0.5") }, trust.Proxies);
    }

    [Fact]
    public void Cidr_networks_are_parsed_for_container_topologies()
    {
        ProxyConfiguration.TrustList trust = ProxyConfiguration.Parse(null, new[] { "172.18.0.0/16" });

        ProxyConfiguration.NetworkPrefix network = Assert.Single(trust.Networks);
        Assert.Equal(IPAddress.Parse("172.18.0.0"), network.Prefix);
        Assert.Equal(16, network.PrefixLength);
    }

    [Fact]
    public void Blank_entries_fall_back_to_the_loopback_default()
    {
        ProxyConfiguration.TrustList trust = ProxyConfiguration.Parse(new[] { "", "   " }, new[] { "" });

        Assert.Equal(2, trust.Proxies.Count);
        Assert.Empty(trust.Networks);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.1.1.1")]
    public void An_invalid_proxy_address_fails_fast(string proxy)
    {
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Parse(new[] { proxy }, null));
    }

    [Theory]
    [InlineData("172.18.0.0")] // no prefix length
    [InlineData("nonsense/16")]
    [InlineData("172.18.0.0/not-a-number")]
    public void An_invalid_network_fails_fast(string network)
    {
        Assert.Throws<InvalidOperationException>(() => ProxyConfiguration.Parse(null, new[] { network }));
    }
}
