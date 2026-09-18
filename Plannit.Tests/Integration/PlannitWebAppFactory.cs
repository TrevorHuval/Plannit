using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Plannit.Services;

namespace Plannit.Tests.Integration;

/// <summary>
/// Boots the real Plannit app in-process against a throwaway SQLite file database.
/// Uses the "Testing" (non-Development) environment so the app's startup migration
/// creates the schema and the dev-data seeder stays off. The rate limiter can be
/// disabled so auth-path requests from unrelated tests don't share the 10/min bucket.
/// Outbound email is always captured in <see cref="Emails"/>; nothing is ever sent.
/// </summary>
public class PlannitWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Absolute path of this factory's private database file.</summary>
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"plannit-test-{Guid.NewGuid():N}.db");

    /// <summary>Disable the global rate limiter (default) so it can't interfere with test traffic.</summary>
    public bool DisableRateLimiter { get; init; } = true;

    /// <summary>Whether the captured mail sender reports itself as configured (affects registration gating).</summary>
    public bool EmailConfigured { get; init; } = true;

    /// <summary>
    /// Extra configuration applied on top of the factory defaults (e.g. <c>PathBase</c>,
    /// <c>AllowRegistration</c>, <c>Identity:RequireConfirmedAccount</c>).
    /// </summary>
    public Dictionary<string, string?> Settings { get; init; } = new();

    /// <summary>Every email the app tried to send through this factory.</summary>
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                // Pooling=False so the file can be deleted on dispose (pooled handles keep it open on Windows).
                ["ConnectionStrings:DefaultConnection"] = $"DataSource={DatabasePath};Pooling=False",
                ["AllowRegistration"] = "true",
                // Most tests just need a signed-in user; the confirmation flow has its own tests.
                ["Identity:RequireConfirmedAccount"] = "false"
            };
            foreach (var (key, value) in Settings)
                values[key] = value;
            config.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            if (DisableRateLimiter)
            {
                services.PostConfigure<RateLimiterOptions>(o => o.GlobalLimiter = null);
            }

            Emails.IsConfigured = EmailConfigured;
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (File.Exists(DatabasePath)) File.Delete(DatabasePath); }
            catch { /* best-effort cleanup of the throwaway test db */ }
        }
    }
}

/// <summary>In-memory stand-in for the SMTP sender. Records messages; never touches the network.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public sealed record Message(string To, string Subject, string Body, bool IsHtml);

    private readonly List<Message> _sent = new();

    public bool IsConfigured { get; set; } = true;

    public IReadOnlyList<Message> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("SMTP is not configured on this server.");
        lock (_sent) _sent.Add(new Message(toEmail, subject, body, isHtml));
        return Task.CompletedTask;
    }

    /// <summary>Extracts the first href from an HTML body, decoding HTML entities.</summary>
    public static string ExtractLink(Message message)
    {
        var m = Regex.Match(message.Body, "href='([^']+)'|href=\"([^\"]+)\"");
        if (!m.Success)
            throw new InvalidOperationException("No link found in email body.");
        return WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
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
    public static async Task RegisterAsync(HttpClient client, string email, string password = TestPassword, string pathBase = "")
    {
        var postResp = await PostRegisterAsync(client, email, password, pathBase);

        // RequireConfirmedAccount = false → registration signs in and redirects.
        if (postResp.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.OK))
            throw new InvalidOperationException($"Registration failed with status {(int)postResp.StatusCode}.");
    }

    /// <summary>Submits the Register form with a valid antiforgery token and returns the raw response.</summary>
    public static async Task<HttpResponseMessage> PostRegisterAsync(HttpClient client, string email, string password = TestPassword, string pathBase = "")
    {
        var getResp = await client.GetAsync($"{pathBase}/Identity/Account/Register");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
            ["__RequestVerificationToken"] = token
        };

        return await client.PostAsync($"{pathBase}/Identity/Account/Register", new FormUrlEncodedContent(form));
    }

    /// <summary>Submits the Login form with a valid antiforgery token and returns the raw response.</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password, string pathBase = "")
    {
        var getResp = await client.GetAsync($"{pathBase}/Identity/Account/Login");
        getResp.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token
        };

        return await client.PostAsync($"{pathBase}/Identity/Account/Login", new FormUrlEncodedContent(form));
    }
}
