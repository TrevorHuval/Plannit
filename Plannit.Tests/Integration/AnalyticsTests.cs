using Microsoft.Extensions.Configuration;
using Plannit.Services;

namespace Plannit.Tests.Integration;

/// <summary>Google Analytics 4 is opt-in by config: no ID means no tag and no extra CSP sources.</summary>
public class AnalyticsTests : IDisposable
{
    private const string PathBase = "/plannit";
    private const string Id = "G-TEST123456";
    private readonly List<PlannitWebAppFactory> _factories = new();

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private PlannitWebAppFactory NewFactory(string? measurementId)
    {
        var factory = new PlannitWebAppFactory { Settings = { ["PathBase"] = "plannit" } };
        if (measurementId is not null) factory.Settings["Analytics:MeasurementId"] = measurementId;
        _factories.Add(factory);
        return factory;
    }

    private static AnalyticsSettings Settings(string? id) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Analytics:MeasurementId"] = id }).Build());

    [Fact]
    public async Task LoginPage_HasTagAndGoogleCspSources_WhenConfigured()
    {
        using var client = NewFactory(Id).CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        var html = await response.Content.ReadAsStringAsync();
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Matches(@"/js/ga-init(\.\w+)?\.js", html);
        Assert.Contains($"data-ga-id=\"{Id}\"", html);
        Assert.Contains("script-src 'self' 'unsafe-inline' https://www.googletagmanager.com", csp);
        Assert.Contains("https://*.google-analytics.com", csp);
        Assert.Contains("connect-src 'self'", csp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task LoginPage_HasNoTagAndNoGoogleCspSources_WhenNotConfigured(string? id)
    {
        using var client = NewFactory(id).CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        var html = await response.Content.ReadAsStringAsync();
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.DoesNotContain("ga-init", html);
        Assert.DoesNotContain("google", csp);
        Assert.DoesNotContain("connect-src", csp);
    }

    [Fact]
    public async Task InitScript_IsServedSameOrigin()
    {
        using var client = NewFactory(Id).CreateClientNoRedirect();

        var js = await client.GetStringAsync($"{PathBase}/js/ga-init.js");

        Assert.Contains("doNotTrack", js);
        Assert.Contains("allow_google_signals: false", js);
        Assert.DoesNotContain("send_page_view: false", js);
    }

    [Theory]
    [InlineData("UA-12345-1")]
    [InlineData("G-abc")]
    [InlineData("G-X'; alert(1)//")]
    public void MalformedId_FailsAtStartup(string id) =>
        Assert.Throws<InvalidOperationException>(() => Settings(id));

    [Fact]
    public void BlankId_IsDisabled() => Assert.False(Settings("  ").Enabled);
}
