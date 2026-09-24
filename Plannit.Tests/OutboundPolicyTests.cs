using System.Net;
using Plannit.Services.Net;

namespace Plannit.Tests;

/// <summary>
/// Unit coverage for the outbound destination policy (audit P1-01). These never touch the
/// network except for resolving names that must map to loopback (e.g. "localhost").
/// </summary>
public class OutboundPolicyTests
{
    private static OutboundPolicy Ai(string[]? allowed = null, string[]? trusted = null) =>
        OutboundPolicy.ForAi(new OutboundOptions { Ai = new OutboundProfile { AllowedHosts = allowed ?? [], TrustedLocalEndpoints = trusted ?? [] } });

    private static OutboundPolicy SimpleFin(string[]? allowed = null) =>
        OutboundPolicy.ForSimpleFin(new OutboundOptions { SimpleFin = new OutboundProfile { AllowedHosts = allowed ?? [] } });

    /// <summary>True when the destination is stopped by either enforcement layer (shape or post-DNS).</summary>
    private static async Task<bool> IsRefusedAsync(OutboundPolicy policy, string url)
    {
        var uri = new Uri(url);
        if (!policy.TryValidate(uri, out _))
            return true;
        try
        {
            await policy.ResolveAsync(uri.DnsSafeHost, uri, CancellationToken.None);
            return false;
        }
        catch (OutboundPolicyException)
        {
            return true;
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // cloud metadata
    [InlineData("100.64.0.1")]        // carrier-grade NAT
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]     // AWS IPv6 metadata
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::127.0.0.1")]       // IPv4-compatible (deprecated)
    [InlineData("64:ff9b::7f00:1")]   // NAT64 of 127.0.0.1
    [InlineData("2002:a00:1::")]      // 6to4 of 10.0.0.1
    [InlineData("2001:db8::1")]       // documentation
    [InlineData("ff02::1")]
    public void NonPublicAddresses_AreRejected(string address) =>
        Assert.False(OutboundPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]        // just outside 172.16/12
    [InlineData("100.128.0.1")]       // just outside 100.64/10
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::808:808")]  // NAT64 of 8.8.8.8
    public void PublicAddresses_AreAllowed(string address) =>
        Assert.True(OutboundPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://api.openai.com/v1/chat/completions")]        // HTTP downgrade
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://10.0.0.5/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/v1")]
    [InlineData("https://[fd00:ec2::254]/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("https://[::ffff:7f00:1]/")]
    [InlineData("https://0x7f000001/")]                              // hex
    [InlineData("https://2130706433/")]                              // decimal
    [InlineData("https://0177.0.0.1/")]                              // octal
    [InlineData("https://127.1/")]                                   // short form
    [InlineData("https://localhost/v1")]                             // name resolving to loopback
    [InlineData("https://api.openai.com:8443/v1")]                   // non-standard port
    [InlineData("ftp://api.openai.com/")]
    public async Task HostileAiDestinations_AreRefused(string url) =>
        Assert.True(await IsRefusedAsync(Ai(), url), $"{url} should be refused.");

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.anthropic.com/v1/messages")]
    [InlineData("https://openrouter.ai/api/v1/chat/completions")]
    public void PublicHttpsDestinations_PassTheShapeCheck(string url) =>
        Assert.True(Ai().TryValidate(new Uri(url), out _));

    [Theory]
    [InlineData("https://bridge.simplefin.org/simplefin/claim/abc", true)]
    [InlineData("https://beta-bridge.simplefin.org/simplefin", true)]
    [InlineData("https://simplefin.org/x", true)]
    [InlineData("https://evilsimplefin.org/x", false)]
    [InlineData("https://simplefin.org.attacker.example/x", false)]
    [InlineData("https://bridge.simplefin.org:8443/x", false)]
    [InlineData("http://bridge.simplefin.org/x", false)]
    [InlineData("https://api.openai.com/x", false)]
    public void SimpleFin_DefaultsToSimpleFinHostsOnly(string url, bool allowed) =>
        Assert.Equal(allowed, SimpleFin().TryValidate(new Uri(url), out _));

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://eu.api.openai.com/v1", true)]              // subdomain of an allowed host
    [InlineData("https://gateway.example.com:8443/v1", true)]
    [InlineData("https://gateway.example.com/v1", false)]           // port pinned to 8443
    [InlineData("https://openrouter.ai/api/v1", false)]             // not listed
    public void AiAllowlist_RestrictsHostsAndPorts(string url, bool allowed) =>
        Assert.Equal(allowed, Ai(allowed: ["api.openai.com", "gateway.example.com:8443"]).TryValidate(new Uri(url), out _));

    [Fact]
    public void AllowlistedHost_StillCannotBeALiteralPrivateAddress() =>
        Assert.False(Ai(allowed: ["127.0.0.1:443"]).TryValidate(new Uri("https://127.0.0.1/"), out _));

    [Fact]
    public async Task AllowlistedHost_ResolvingToLoopback_IsRefusedAfterDns() =>
        Assert.True(await IsRefusedAsync(Ai(allowed: ["localhost"]), "https://localhost/v1"));

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1/chat/completions", true)]
    [InlineData("http://127.0.0.1:11434/anything", true)]
    [InlineData("https://127.0.0.1:11434/v1", false)]               // scheme differs
    [InlineData("http://127.0.0.1:11435/v1", false)]                // port differs
    [InlineData("http://localhost:11434/v1", false)]                // host differs
    [InlineData("http://10.0.0.1:11434/v1", false)]
    public async Task TrustedLocalEndpoint_IsAnExactOriginMatch(string url, bool allowed)
    {
        var policy = Ai(trusted: ["http://127.0.0.1:11434"]);
        Assert.Equal(!allowed, await IsRefusedAsync(policy, url));
    }

    [Fact]
    public void SafeMessage_HidesTransportDetails_AndPassesPolicyReasons()
    {
        var transport = new HttpRequestException("Connection refused (10.0.0.7:6379)");
        Assert.DoesNotContain("10.0.0.7", OutboundHttp.SafeMessage(transport, "X"));

        var wrapped = new HttpRequestException("wrapper", new OutboundPolicyException("X address was refused: nope"));
        Assert.Equal("X address was refused: nope", OutboundHttp.SafeMessage(wrapped, "X"));

        var upstream = new UpstreamResponseException("X", HttpStatusCode.BadRequest);
        Assert.Equal("X returned HTTP 400.", OutboundHttp.SafeMessage(upstream, "X"));
    }
}
