using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Services;

namespace Plannit.Tests.Integration;

/// <summary>
/// What must hold before password sign-up is opened to strangers: account email cannot be turned into
/// a mail cannon aimed at arbitrary addresses, and the number of accounts can be capped.
/// </summary>
public class PublicSignupSafetyTests : IDisposable
{
    private readonly List<PlannitWebAppFactory> _factories = new();

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private PlannitWebAppFactory NewFactory(params (string Key, string Value)[] settings)
    {
        var factory = new PlannitWebAppFactory { Settings = { ["Identity:RequireConfirmedAccount"] = "true" } };
        foreach (var (key, value) in settings) factory.Settings[key] = value;
        _factories.Add(factory);
        return factory;
    }

    private static async Task<HttpResponseMessage> ResendAsync(HttpClient client, string email)
    {
        var page = await client.GetStringAsync("/Identity/Account/ResendEmailConfirmation");
        return await client.PostAsync("/Identity/Account/ResendEmailConfirmation", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email, ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));
    }

    [Fact]
    public async Task AccountMail_ToOneAddress_IsLimitedPerHour_AndDroppedSilently()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();
        const string victim = "victim@example.invalid";
        var registered = await HttpTestHelpers.PostRegisterAsync(client, victim);   // mail 1
        Assert.True(registered.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)                                                    // resends 2..7
            statuses.Add((await ResendAsync(client, victim)).StatusCode);

        Assert.Equal(3, factory.Emails.Sent.Count(m => m.To == victim));               // capped at 3 per hour
        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));                 // identical response, never an error
    }

    [Fact]
    public async Task AccountMail_LimitIsPerRecipient_NotPerServer()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();
        await HttpTestHelpers.PostRegisterAsync(client, "first@example.invalid");
        for (var i = 0; i < 5; i++) await ResendAsync(client, "first@example.invalid");

        await HttpTestHelpers.PostRegisterAsync(client, "second@example.invalid");

        Assert.Equal(3, factory.Emails.Sent.Count(m => m.To == "first@example.invalid"));
        Assert.Equal(1, factory.Emails.Sent.Count(m => m.To == "second@example.invalid"));
    }

    [Fact]
    public async Task AccountMail_IsCapped_ByTheGlobalDailyLimit()
    {
        var factory = NewFactory(("Email:GlobalDailyLimit", "2"));
        using var client = factory.CreateClientNoRedirect();

        foreach (var name in new[] { "a", "b", "c", "d" })
            await HttpTestHelpers.PostRegisterAsync(client, $"{name}@example.invalid");

        Assert.Equal(2, factory.Emails.Sent.Count);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.Equal(4, await users.Users.CountAsync()); // accounts are still created; only the mail is held back
    }

    [Fact]
    public async Task AccountMail_RecordsNoAddresses()
    {
        var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();
        await HttpTestHelpers.PostRegisterAsync(client, "private@example.invalid");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.EmailDispatches.IgnoreQueryFilters().SingleAsync();

        Assert.Equal("Account", row.Kind);
        Assert.Null(row.UserId);
        Assert.Equal(64, row.RecipientHash!.Length);
        Assert.DoesNotContain("private", row.RecipientHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendFailure_DuringSignup_DoesNotBreakTheSignup()
    {
        var factory = NewFactory();
        factory.Emails.IsConfigured = true;
        factory.Emails.FailSends = true;
        using var client = factory.CreateClientNoRedirect();

        var response = await HttpTestHelpers.PostRegisterAsync(client, "flaky@example.invalid");

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task MaxUsers_StopsPasswordSignup_WhenFull()
    {
        var factory = NewFactory(("Registration:MaxUsers", "2"));
        using var client = factory.CreateClientNoRedirect();
        await HttpTestHelpers.PostRegisterAsync(client, "one@example.invalid");
        await HttpTestHelpers.PostRegisterAsync(client, "two@example.invalid");

        var page = await client.GetAsync("/Identity/Account/Register");

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("full", await page.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().Users.CountAsync());
    }

    [Fact]
    public async Task MaxUsers_UnsetOrZero_MeansNoLimit()
    {
        var factory = NewFactory(("Registration:MaxUsers", "0"));
        using var client = factory.CreateClientNoRedirect();
        for (var i = 0; i < 4; i++)
            Assert.True((await HttpTestHelpers.PostRegisterAsync(client, $"user{i}@example.invalid")).StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
    }
}
