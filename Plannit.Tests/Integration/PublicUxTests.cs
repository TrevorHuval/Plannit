using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Tests.Integration;

/// <summary>
/// Audit P2-09 and the cookie/CSP items of the deployment checklist: public pages carry no scaffold
/// text, describe real behaviour, show only flows that exist, and every cookie is HTTPS-only.
/// All requests run under the /plannit PathBase used in production.
/// </summary>
public class PublicUxTests : IDisposable
{
    private const string PathBase = "/plannit";
    private readonly List<PlannitWebAppFactory> _factories = new();

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private PlannitWebAppFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var factory = new PlannitWebAppFactory { Settings = { ["PathBase"] = "plannit" } };
        foreach (var (key, value) in settings) factory.Settings[key] = value;
        _factories.Add(factory);
        return factory;
    }

    // ---------------------------------------------------------------------------------------
    // Registration page
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RegisterPage_StatesTheEnforcedPasswordRules_NotTheScaffoldDefault()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var html = await client.GetStringAsync($"{PathBase}/Identity/Account/Register");

        Assert.Contains("at least 12 characters", html);
        Assert.Contains("an uppercase letter", html);
        Assert.DoesNotContain("data-val-length-min=\"6\"", html);
        Assert.DoesNotContain("at least 6", html);
        Assert.Contains($"{PathBase}/Home/Privacy", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Short1!aaaa", false)]     // 11 characters
    [InlineData("Twelve12chr!", true)]     // 12 characters
    public async Task RegisterPage_EnforcesTheTwelveCharacterMinimum(string password, bool accepted)
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var response = await HttpTestHelpers.PostRegisterAsync(client, $"pw-{accepted}@example.invalid", password, PathBase);

        if (accepted)
            Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        else
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Login page: only flows that exist
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LoginPage_HasNoScaffoldInstructions_AndNoProviderButtons_WhenNoneConfigured()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var html = await client.GetStringAsync($"{PathBase}/Identity/Account/Login");

        Assert.DoesNotContain("go.microsoft.com", html);
        Assert.DoesNotContain("external authentication services", html);
        Assert.DoesNotContain("Continue with", html);
        Assert.DoesNotContain("Apple", html);
        Assert.Contains("Privacy", html);
    }

    [Fact]
    public async Task Csp_AllowsOnlyTheConfiguredProviderOrigins()
    {
        // (Provider buttons are registered when the host is built, so they are covered by the fake-provider
        // tests; the CSP is computed from live configuration and can be checked here.)
        var factory = NewFactory(("Authentication:Google:ClientId", "test-client"), ("Authentication:Google:ClientSecret", "test-secret"));
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("https://accounts.google.com", csp);
        Assert.DoesNotContain("appleid.apple.com", csp);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("id", "secret", "https://accounts.google.com")]
    [InlineData("id", null, "")]
    public void FormActionSources_ListsOnlyConfiguredGoogle(string? id, string? secret, string expected)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:ClientId"] = id, ["Authentication:Google:ClientSecret"] = secret
        }).Build();

        Assert.Equal(expected, Plannit.Services.ExternalLoginProviders.FormActionSources(config));
    }

    [Fact]
    public void FormActionSources_AddsApple_OnlyWhenEveryAppleSettingIsPresent()
    {
        var partial = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Apple:ClientId"] = "svc", ["Authentication:Apple:TeamId"] = "team"
        }).Build();
        var full = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Apple:ClientId"] = "svc", ["Authentication:Apple:TeamId"] = "team",
            ["Authentication:Apple:KeyId"] = "key", ["Authentication:Apple:PrivateKey"] = "pem"
        }).Build();

        Assert.Equal("", Plannit.Services.ExternalLoginProviders.FormActionSources(partial));
        Assert.Equal("https://appleid.apple.com", Plannit.Services.ExternalLoginProviders.FormActionSources(full));
    }

    [Fact]
    public async Task Csp_AllowsNoExternalFormTargets_WhenNoProviderIsConfigured()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.DoesNotContain("google", csp);
        Assert.DoesNotContain("apple", csp);
    }

    [Fact]
    public async Task ResendConfirmationLink_IsHidden_WhenNoConfirmationFlowExists()
    {
        var personal = NewFactory(("AllowRegistration", "false"));
        var publicMode = NewFactory(("AllowRegistration", "true"), ("Identity:RequireConfirmedAccount", "true"));
        using var personalClient = personal.CreateClientNoRedirect();
        using var publicClient = publicMode.CreateClientNoRedirect();

        Assert.DoesNotContain("Resend confirmation", await personalClient.GetStringAsync($"{PathBase}/Identity/Account/Login"));
        Assert.Contains("Resend confirmation", await publicClient.GetStringAsync($"{PathBase}/Identity/Account/Login"));
    }

    // ---------------------------------------------------------------------------------------
    // Privacy page
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PrivacyPage_IsPublic_HasNoTemplateText_AndDescribesRealBehaviour()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Home/Privacy");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Use this page to detail", html);
        Assert.Contains("What is stored", html);
        Assert.Contains("salted hash of your password", html);
        Assert.Contains("stop being usable after 2 hours", html);
        Assert.Contains("deleted after 90 days", html);
        Assert.Contains("AI categorization", html);
        Assert.Contains("does not send balances", html);
        Assert.Contains("Delete your account", html);
        Assert.Contains("partial JSON copy", html);
        Assert.Contains("no advertising, analytics or tracking", html);
    }

    [Fact]
    public async Task PrivacyPage_OnlyDescribesFeaturesThatAreActive()
    {
        var plain = NewFactory(("BankSync:Enabled", "false"));
        using var plainClient = plain.CreateClientNoRedirect();
        var plainHtml = await plainClient.GetStringAsync($"{PathBase}/Home/Privacy");

        Assert.DoesNotContain("SimpleFIN", plainHtml);
        Assert.DoesNotContain("sign in with Google", plainHtml);

        var full = NewFactory(("BankSync:Enabled", "true"), ("Authentication:Google:ClientId", "c"), ("Authentication:Google:ClientSecret", "s"));
        using var fullClient = full.CreateClientNoRedirect();
        var fullHtml = await fullClient.GetStringAsync($"{PathBase}/Home/Privacy");

        Assert.Contains("Bank connections (SimpleFIN)", fullHtml);
        Assert.Contains("sign in with Google", fullHtml);
        Assert.DoesNotContain("Apple", fullHtml);
    }

    [Fact]
    public async Task PrivacyPage_ShowsOperatorContactAndBackupNote_WhenConfigured()
    {
        var factory = NewFactory(
            ("Privacy:OperatorName", "Example Operator"),
            ("Privacy:ContactEmail", "privacy@example.invalid"),
            ("Privacy:BackupRetention", "Encrypted backups are kept for 30 days."));
        using var client = factory.CreateClientNoRedirect();

        var html = await client.GetStringAsync($"{PathBase}/Home/Privacy");

        Assert.Contains("run by Example Operator", html);
        Assert.Contains("mailto:privacy@example.invalid", html);
        Assert.Contains("Encrypted backups are kept for 30 days.", html);
        Assert.DoesNotContain("remain in older backups until they are rotated out", html);
    }

    [Fact]
    public async Task PrivacyPage_WorksOnMobilePageStructure_AndIsLinkedFromPublicPages()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var login = await client.GetStringAsync($"{PathBase}/Identity/Account/Login");
        var privacy = await client.GetStringAsync($"{PathBase}/Home/Privacy");

        Assert.Contains("name=\"viewport\"", privacy);
        Assert.Contains($"{PathBase}/Home/Privacy", login, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{PathBase}/Home/Privacy", privacy, StringComparison.OrdinalIgnoreCase); // sidebar link
    }

    // ---------------------------------------------------------------------------------------
    // The privacy page promises deletion removes your data: prove it
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletingAnAccount_RemovesEverythingItOwns_AndNothingElse()
    {
        var factory = NewFactory();
        string keeperId, doomedId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var keeper = new IdentityUser { UserName = "keeper@example.invalid", Email = "keeper@example.invalid" };
            var doomed = new IdentityUser { UserName = "doomed@example.invalid", Email = "doomed@example.invalid" };
            Assert.True((await users.CreateAsync(keeper, HttpTestHelpers.TestPassword)).Succeeded);
            Assert.True((await users.CreateAsync(doomed, HttpTestHelpers.TestPassword)).Succeeded);
            keeperId = keeper.Id; doomedId = doomed.Id;

            foreach (var owner in new[] { keeper, doomed })
            {
                db.SetCurrentUser(owner.Id);
                var parent = new Category { UserId = owner.Id, Name = "Parent" };
                db.Categories.Add(parent);
                await db.SaveChangesAsync();
                var child = new Category { UserId = owner.Id, Name = "Child", ParentId = parent.Id };
                var account = new Account { UserId = owner.Id, Name = "Checking", Type = AccountType.Checking };
                db.AddRange(child, account);
                await db.SaveChangesAsync();
                db.Add(new Transaction { AccountId = account.Id, Date = new DateOnly(2026, 1, 5), Amount = -5m, Description = "x", OriginalDescription = "x", CategoryId = child.Id });
                db.Add(new BalanceSnapshot { AccountId = account.Id, Date = new DateOnly(2026, 1, 31), Balance = 10m });
                db.Add(new Budget { UserId = owner.Id, CategoryId = child.Id, MonthlyAmount = 50m, StartMonth = new DateOnly(2026, 1, 1) });
                db.Add(new ProjectionScenario { UserId = owner.Id, Name = "S", BirthYear = 1990, RetirementAge = 65, LifeExpectancy = 90, AnnualRetirementSpending = 1m, InflationRate = 0.03m });
                db.Add(new NotificationPreferences { UserId = owner.Id, Email = "n@example.invalid" });
                db.Add(new AiSettings { UserId = owner.Id, Provider = AiProvider.None });
                db.Add(new TempUpload { Id = Guid.NewGuid(), UserId = owner.Id, AccountId = account.Id, Kind = "CsvMap", Extension = ".csv", CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddHours(1) });
                db.Add(new EmailDispatch { UserId = owner.Id, Kind = "Alert", SentUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByIdAsync(doomedId))!)).Succeeded);
        }

        using var verify = factory.Services.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        async Task<int> CountFor(string userId) =>
            await check.Accounts.IgnoreQueryFilters().CountAsync(a => a.UserId == userId)
            + await check.Categories.IgnoreQueryFilters().CountAsync(c => c.UserId == userId)
            + await check.Budgets.IgnoreQueryFilters().CountAsync(b => b.UserId == userId)
            + await check.ProjectionScenarios.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)
            + await check.NotificationPreferences.IgnoreQueryFilters().CountAsync(n => n.UserId == userId)
            + await check.AiSettings.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)
            + await check.TempUploads.IgnoreQueryFilters().CountAsync(u => u.UserId == userId)
            + await check.EmailDispatches.IgnoreQueryFilters().CountAsync(d => d.UserId == userId)
            + await check.Transactions.IgnoreQueryFilters().CountAsync(t => t.Account.UserId == userId)
            + await check.BalanceSnapshots.IgnoreQueryFilters().CountAsync(s => s.Account.UserId == userId);

        Assert.Equal(0, await CountFor(doomedId));
        // 2 categories, 1 account, 1 transaction, 1 snapshot, budget, scenario, prefs, AI settings, upload, email log.
        Assert.Equal(11, await CountFor(keeperId));
    }

    // ---------------------------------------------------------------------------------------
    // Cookies
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cookies_AreSecureAndHttpOnly_WhenSecureIsRequired()
    {
        var factory = NewFactory(("Cookies:RequireSecure", "true"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var loginPage = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        var pageCookies = loginPage.Headers.GetValues("Set-Cookie").ToList();
        Assert.NotEmpty(pageCookies);
        Assert.All(pageCookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));

        // Sign in and check the auth cookie too.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            await users.CreateAsync(new IdentityUser { UserName = "secure@example.invalid", Email = "secure@example.invalid", EmailConfirmed = true }, HttpTestHelpers.TestPassword);
        }
        var login = await HttpTestHelpers.PostLoginAsync(client, "secure@example.invalid", HttpTestHelpers.TestPassword, PathBase);
        var authCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Identity.Application="));
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssumeHttps_MakesFormCookiesSecure_EvenWhenTheProxySchemeIsNotForwarded()
    {
        // The proxy terminated TLS but the app received plain http and no trusted X-Forwarded-Proto.
        var factory = NewFactory(("Cookies:RequireSecure", "true"), ("Hosting:AssumeHttps", "true"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(response.Headers.GetValues("Set-Cookie"), c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithoutAssumeHttps_APlainHttpRequest_StillWorks_NotA500()
    {
        // Misdetected schemes must degrade to a working (non-Secure antiforgery) page, never break sign-in.
        var factory = NewFactory(("Cookies:RequireSecure", "true"));
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync($"{PathBase}/Identity/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
