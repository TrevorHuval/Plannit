using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Plannit.Tests.Integration;

/// <summary>
/// Boots the real Plannit app in-process against a throwaway SQLite file database.
/// Uses the "Testing" (non-Development) environment so the app's startup migration
/// creates the schema and the dev-data seeder stays off. The rate limiter can be
/// disabled so auth-path requests from unrelated tests don't share the 10/min bucket.
/// </summary>
public class PlannitWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"plannit-test-{Guid.NewGuid():N}.db");

    /// <summary>Disable the global rate limiter (default) so it can't interfere with test traffic.</summary>
    public bool DisableRateLimiter { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"DataSource={_dbPath};Cache=Shared",
                ["AllowRegistration"] = "true"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            if (DisableRateLimiter)
            {
                services.PostConfigure<RateLimiterOptions>(o => o.GlobalLimiter = null);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
            catch { /* best-effort cleanup of the throwaway test db */ }
        }
    }
}

/// <summary>HTTP choreography shared by the integration tests: antiforgery + Identity register/login.</summary>
public static class HttpTestHelpers
{
    // Strong enough for the Identity policy (length 12, upper/lower/digit/symbol).
    public const string TestPassword = "IntegrationTest1!";

    public static HttpClient CreateClientNoRedirect(this PlannitWebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static string ExtractAntiforgeryToken(string html) =>
        ExtractHiddenField(html, "__RequestVerificationToken");

    public static string ExtractHiddenField(string html, string name)
    {
        // Attribute order in the rendered <input> isn't guaranteed, so match either ordering.
        var m = Regex.Match(html, $@"name=""{Regex.Escape(name)}""[^>]*\bvalue=""([^""]*)""");
        if (!m.Success)
            m = Regex.Match(html, $@"value=""([^""]*)""[^>]*name=""{Regex.Escape(name)}""");
        if (!m.Success)
            throw new InvalidOperationException($"Hidden field '{name}' not found in HTML.");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    /// <summary>Registers a user through the scaffolded Identity page; the client is signed in on return.</summary>
    public static async Task RegisterAsync(HttpClient client, string email, string password = TestPassword)
    {
        var getResp = await client.GetAsync("/Identity/Account/Register");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
            ["__RequestVerificationToken"] = token
        };

        var postResp = await client.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(form));

        // RequireConfirmedAccount = false → registration signs in and redirects.
        if (postResp.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.OK))
            throw new InvalidOperationException($"Registration failed with status {(int)postResp.StatusCode}.");
    }
}
