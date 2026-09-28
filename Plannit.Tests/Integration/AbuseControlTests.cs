using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Services.Ai;

namespace Plannit.Tests.Integration;

/// <summary>
/// Secure-behaviour regression tests for audit P2-08: notification email goes only to verified
/// recipients within send budgets, expensive endpoints have their own per-user limits, staged
/// uploads are capped per user, the Claude CLI is not offered to arbitrary users, and projection
/// comparison work is bounded. Email is always captured by the factory's in-memory sink.
/// </summary>
public class AbuseControlTests : IDisposable
{
    private readonly List<PlannitWebAppFactory> _factories = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files) { try { File.Delete(f); } catch { /* best effort */ } }
        foreach (var f in _factories) f.Dispose();
    }

    private PlannitWebAppFactory NewFactory(bool rateLimiter = false, params (string Key, string Value)[] settings)
    {
        var factory = new PlannitWebAppFactory { DisableRateLimiter = !rateLimiter, Settings = { ["Email:UserCooldownSeconds"] = "0" } };
        foreach (var (key, value) in settings) factory.Settings[key] = value;
        _factories.Add(factory);
        return factory;
    }

    private sealed record Actor(HttpClient Client, string UserId, string Email, int AccountId);

    private static async Task<Actor> NewActorAsync(PlannitWebAppFactory factory, string email)
    {
        var client = factory.CreateClientNoRedirect();
        await HttpTestHelpers.RegisterAsync(client, email);
        using var scope = factory.Services.CreateScope();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(user.Id);
        var account = new Account { UserId = user.Id, Name = "Main", Type = AccountType.Checking };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return new Actor(client, user.Id, email, account.Id);
    }

    // ---------------------------------------------------------------------------------------
    // Notification email: verification, recipient binding, budgets
    // ---------------------------------------------------------------------------------------

    private static async Task SaveNotificationEmailAsync(Actor who, string address, bool enabled = true)
    {
        var page = await who.Client.GetStringAsync("/Settings/Notifications");
        var response = await who.Client.PostAsync("/Settings/SaveNotifications", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["Email"] = address, ["EmailEnabled"] = enabled ? "true" : "false", ["DigestMode"] = "0",
            ["StaleAccountEnabled"] = "true"
        }));
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"Save returned {(int)response.StatusCode}.");
    }

    private static async Task<string> PostActionAsync(Actor who, string action)
    {
        var page = await who.Client.GetStringAsync("/Settings/Notifications");
        var response = await who.Client.PostAsync($"/Settings/{action}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<NotificationPreferences> PrefsAsync(PlannitWebAppFactory factory, Actor who)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        return await db.NotificationPreferences.AsNoTracking().SingleAsync();
    }

    /// <summary>Mail the notification features sent; excludes Identity's own registration message.</summary>
    private static List<CapturingEmailSender.Message> Mail(PlannitWebAppFactory factory) =>
        factory.Emails.Sent.Where(m => m.Subject != "Confirm your email").ToList();

    private static async Task<string> VerifyAsync(PlannitWebAppFactory factory, Actor who, string address)
    {
        await SaveNotificationEmailAsync(who, address);
        await PostActionAsync(who, "SendVerificationEmail");
        var mail = Mail(factory).Last(m => m.To == address);
        var link = Regex.Match(mail.Body, @"https?://\S+").Value;
        var response = await who.Client.GetAsync(new Uri(link).PathAndQuery);
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        return link;
    }

    private static async Task SeedStaleAccountAsync(PlannitWebAppFactory factory, Actor who)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = who.AccountId, Date = DateOnly.FromDateTime(DateTime.Today).AddDays(-90), Balance = 100m });
        await db.SaveChangesAsync();
    }

    private static async Task<int> RunAlertsAsync(PlannitWebAppFactory factory, Actor who)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SetCurrentUser(who.UserId);
        return await scope.ServiceProvider.GetRequiredService<NotificationService>().RunDailyChecksAsync(who.UserId);
    }

    [Fact]
    public async Task UnverifiedRecipient_ReceivesNoTestEmail_AndNoAlertEmail()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "unverified@example.invalid");
        await SaveNotificationEmailAsync(a, "victim@example.invalid");
        await SeedStaleAccountAsync(factory, a);

        var result = await PostActionAsync(a, "SendTestEmail");
        var alerts = await RunAlertsAsync(factory, a);

        Assert.Contains("Verify your email address first", result);
        Assert.True(alerts > 0); // the alert exists in-app...
        Assert.Empty(Mail(factory)); // ...but nothing was mailed to the unverified address
    }

    [Fact]
    public async Task Verification_SendsOneLink_ToTheSavedAddress_AndOwnerCanConfirm()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "verify-flow@example.invalid");
        await SaveNotificationEmailAsync(a, "alerts@example.invalid");

        var page = await PostActionAsync(a, "SendVerificationEmail");

        var mail = Assert.Single(Mail(factory));
        Assert.Equal("alerts@example.invalid", mail.To);
        Assert.Contains("Verification email sent", page);
        var link = Regex.Match(mail.Body, @"https?://\S+").Value;
        Assert.Contains("/Settings/VerifyNotificationEmail?token=", link);
        Assert.Null((await PrefsAsync(factory, a)).EmailVerifiedUtc);
        Assert.NotNull((await PrefsAsync(factory, a)).EmailVerificationTokenHash);
        Assert.DoesNotContain(new Uri(link).Query.Split('=')[1], (await PrefsAsync(factory, a)).EmailVerificationTokenHash!); // only a hash is stored

        var confirmed = await a.Client.GetAsync(new Uri(link).PathAndQuery);

        Assert.True(confirmed.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        var prefs = await PrefsAsync(factory, a);
        Assert.NotNull(prefs.EmailVerifiedUtc);
        Assert.Null(prefs.EmailVerificationTokenHash);
    }

    [Fact]
    public async Task VerifiedRecipient_ReceivesTestAndAlertEmail()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "verified@example.invalid");
        await VerifyAsync(factory, a, "me@example.invalid");
        var before = Mail(factory).Count;
        await SeedStaleAccountAsync(factory, a);

        var test = await PostActionAsync(a, "SendTestEmail");
        await RunAlertsAsync(factory, a);

        Assert.Contains("Test email sent", test);
        var sent = Mail(factory).Skip(before).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Equal("me@example.invalid", m.To));
    }

    [Fact]
    public async Task VerificationToken_CannotBeUsedByAnotherUser_OrTwice_OrWhenInvalid()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "token-a@example.invalid");
        var b = await NewActorAsync(factory, "token-b@example.invalid");
        await SaveNotificationEmailAsync(a, "a-alerts@example.invalid");
        await SaveNotificationEmailAsync(b, "b-alerts@example.invalid");
        await PostActionAsync(a, "SendVerificationEmail");
        var link = new Uri(Regex.Match(Mail(factory).Single().Body, @"https?://\S+").Value).PathAndQuery;

        await b.Client.GetAsync(link);                                                   // B presents A's token
        await a.Client.GetAsync("/Settings/VerifyNotificationEmail?token=not-the-token"); // bogus token
        using var anonymous = factory.CreateClientNoRedirect();
        var anon = await anonymous.GetAsync(link);                                       // not signed in

        Assert.Null((await PrefsAsync(factory, b)).EmailVerifiedUtc);
        Assert.Null((await PrefsAsync(factory, a)).EmailVerifiedUtc);
        Assert.Contains("/Identity/Account/Login", anon.Headers.Location!.ToString());

        await a.Client.GetAsync(link);
        Assert.NotNull((await PrefsAsync(factory, a)).EmailVerifiedUtc);
        var replayPrefs = await PrefsAsync(factory, a);
        await SaveNotificationEmailAsync(a, "a-alerts@example.invalid"); // unchanged address keeps verification
        Assert.NotNull((await PrefsAsync(factory, a)).EmailVerifiedUtc);
        await a.Client.GetAsync(link);                                    // replay is a no-op
        Assert.Equal(replayPrefs.EmailVerifiedUtc, (await PrefsAsync(factory, a)).EmailVerifiedUtc);
    }

    [Fact]
    public async Task ExpiredVerificationToken_IsRejected()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "expired-token@example.invalid");
        await SaveNotificationEmailAsync(a, "x@example.invalid");
        await PostActionAsync(a, "SendVerificationEmail");
        var link = new Uri(Regex.Match(Mail(factory).Single().Body, @"https?://\S+").Value).PathAndQuery;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SetCurrentUser(a.UserId);
            (await db.NotificationPreferences.SingleAsync()).EmailVerificationExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await a.Client.GetAsync(link);

        Assert.Null((await PrefsAsync(factory, a)).EmailVerifiedUtc);
    }

    [Fact]
    public async Task ChangingTheAddress_RevokesVerification_AndStopsAlertMail()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "change@example.invalid");
        await VerifyAsync(factory, a, "first@example.invalid");
        await SaveNotificationEmailAsync(a, "second@example.invalid");
        var before = Mail(factory).Count;
        await SeedStaleAccountAsync(factory, a);

        await RunAlertsAsync(factory, a);

        Assert.Null((await PrefsAsync(factory, a)).EmailVerifiedUtc);
        Assert.Equal(before, Mail(factory).Count);
    }

    [Fact]
    public async Task VerificationMail_HasCooldown_AndDailyBudget()
    {
        var factory = NewFactory(false, ("Email:UserCooldownSeconds", "3600"), ("Email:UserDailyLimit", "5"));
        var a = await NewActorAsync(factory, "cooldown@example.invalid");
        await SaveNotificationEmailAsync(a, "c@example.invalid");

        await PostActionAsync(a, "SendVerificationEmail");
        var second = await PostActionAsync(a, "SendVerificationEmail");

        Assert.Single(Mail(factory));
        Assert.Contains("Please wait", second);

        var budgeted = NewFactory(false, ("Email:UserDailyLimit", "2"));
        var b = await NewActorAsync(budgeted, "budget@example.invalid");
        await SaveNotificationEmailAsync(b, "b@example.invalid");
        await PostActionAsync(b, "SendVerificationEmail");
        await PostActionAsync(b, "SendVerificationEmail");
        var third = await PostActionAsync(b, "SendVerificationEmail");

        Assert.Equal(2, Mail(budgeted).Count);
        Assert.Contains("limit for verification and test emails", third);
    }

    [Fact]
    public async Task GlobalDailyCap_StopsMailAcrossUsers()
    {
        var factory = NewFactory(false, ("Email:GlobalDailyLimit", "1"));
        var a = await NewActorAsync(factory, "global-a@example.invalid");
        var b = await NewActorAsync(factory, "global-b@example.invalid");
        await SaveNotificationEmailAsync(a, "ga@example.invalid");
        await SaveNotificationEmailAsync(b, "gb@example.invalid");

        await PostActionAsync(a, "SendVerificationEmail");
        var refused = await PostActionAsync(b, "SendVerificationEmail");

        Assert.Single(Mail(factory));
        Assert.Contains("daily email limit", refused);
    }

    [Fact]
    public async Task AlertMail_IsCappedPerUser_ButAlertsStillAppearInApp()
    {
        var factory = NewFactory(false, ("Email:AlertDailyLimit", "1"));
        var a = await NewActorAsync(factory, "alert-cap@example.invalid");
        await VerifyAsync(factory, a, "cap@example.invalid");
        var before = Mail(factory).Count;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SetCurrentUser(a.UserId);
            for (var i = 0; i < 3; i++)
            {
                var account = new Account { UserId = a.UserId, Name = $"Stale{i}", Type = AccountType.Savings };
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = account.Id, Date = DateOnly.FromDateTime(DateTime.Today).AddDays(-90), Balance = 1m });
            }
            await db.SaveChangesAsync();
        }

        var created = await RunAlertsAsync(factory, a);

        Assert.True(created >= 3);
        Assert.Equal(1, Mail(factory).Count - before);
    }

    // ---------------------------------------------------------------------------------------
    // Rate limits for expensive endpoints
    // ---------------------------------------------------------------------------------------

    private static DefaultHttpContext Context(string path, string method = "POST", string? userId = null, string ip = "203.0.113.50")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (userId is not null)
            context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId)], "test"));
        return context;
    }

    [Theory]
    [InlineData("/Transactions/Import", "POST", true)]
    [InlineData("/transactions/confirmimport", "POST", true)]
    [InlineData("/Transactions/ConfirmSnapshotImport", "POST", true)]
    [InlineData("/SmartCategorize/Review", "POST", true)]
    [InlineData("/Settings/TestConnection", "POST", true)]
    [InlineData("/Settings/SendTestEmail", "POST", true)]
    [InlineData("/Settings/SendVerificationEmail", "POST", true)]
    [InlineData("/Sync/SyncNow", "POST", true)]
    [InlineData("/Projections/Compare", "GET", true)]
    [InlineData("/Projections/Results/3", "GET", true)]
    [InlineData("/Transactions/Import", "GET", false)]      // just renders the upload form
    [InlineData("/Transactions", "GET", false)]
    [InlineData("/Projections", "GET", false)]
    [InlineData("/Dashboard", "GET", false)]
    public void ExpensiveEndpoints_AreClassified(string path, string method, bool expensive) =>
        Assert.Equal(expensive, RateLimiterConfiguration.IsExpensiveRequest(path, method));

    [Fact]
    public async Task ExpensiveBudget_IsPerUser_NotPerSharedIp_AndSeparateFromNavigation()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();

        for (var i = 0; i < RateLimiterConfiguration.ExpensivePermitLimit; i++)
        {
            using var lease = await limiter.AcquireAsync(Context("/Settings/TestConnection", userId: "u1"));
            Assert.True(lease.IsAcquired, $"request {i + 1}");
        }
        using var over = await limiter.AcquireAsync(Context("/Settings/TestConnection", userId: "u1"));
        using var otherUserSameIp = await limiter.AcquireAsync(Context("/Settings/TestConnection", userId: "u2"));
        using var navigation = await limiter.AcquireAsync(Context("/Dashboard", "GET", userId: "u1"));

        Assert.False(over.IsAcquired);
        Assert.True(otherUserSameIp.IsAcquired);
        Assert.True(navigation.IsAcquired);
    }

    [Fact]
    public async Task ExpensiveRequests_AreCappedInConcurrency_PerUser()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();
        var held = new List<System.Threading.RateLimiting.RateLimitLease>();
        for (var i = 0; i < RateLimiterConfiguration.ExpensiveConcurrency; i++)
            held.Add(await limiter.AcquireAsync(Context("/Projections/Compare", "GET", "busy")));

        using var third = await limiter.AcquireAsync(Context("/Projections/Compare", "GET", "busy"));
        using var otherUser = await limiter.AcquireAsync(Context("/Projections/Compare", "GET", "calm"));
        using var ordinary = await limiter.AcquireAsync(Context("/Transactions", "GET", "busy"));

        Assert.All(held, l => Assert.True(l.IsAcquired));
        Assert.False(third.IsAcquired);
        Assert.True(otherUser.IsAcquired);
        Assert.True(ordinary.IsAcquired);

        held[0].Dispose(); // a finished request frees a slot
        using var afterRelease = await limiter.AcquireAsync(Context("/Projections/Compare", "GET", "busy"));
        Assert.True(afterRelease.IsAcquired);
        foreach (var l in held) l.Dispose();
    }

    [Fact]
    public async Task ExpensiveEndpoint_Returns429_ThroughThePipeline_PerUser()
    {
        var factory = NewFactory(rateLimiter: true);
        var a = await NewActorAsync(factory, "rl-a@example.invalid");
        var b = await NewActorAsync(factory, "rl-b@example.invalid");

        HttpStatusCode last = 0;
        for (var i = 0; i <= RateLimiterConfiguration.ExpensivePermitLimit; i++)
        {
            var response = await a.Client.PostAsync("/Settings/TestConnection", new FormUrlEncodedContent(new Dictionary<string, string>()));
            last = response.StatusCode;
            if (i < RateLimiterConfiguration.ExpensivePermitLimit) Assert.NotEqual(HttpStatusCode.TooManyRequests, last);
        }

        var otherUser = await b.Client.PostAsync("/Settings/TestConnection", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherUser.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Upload quota
    // ---------------------------------------------------------------------------------------

    private string TempDir(PlannitWebAppFactory factory) =>
        Path.Combine(factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath, "TempUploads");

    private async Task<(HttpResponseMessage Response, string? Id)> UploadAsync(PlannitWebAppFactory factory, Actor who, string content = "Date,Description,Amount\n01/15/2026,X,-1.00\n")
    {
        var page = await who.Client.GetStringAsync("/Transactions/Import");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpTestHelpers.ExtractAntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent(who.AccountId.ToString()), "AccountId" },
            { new StringContent("false"), "PositionsStatement" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "Files", "s.csv" }
        };
        var response = await who.Client.PostAsync("/Transactions/Import", form);
        if (response.StatusCode != HttpStatusCode.OK) return (response, null);
        var id = HttpTestHelpers.ExtractHiddenField(await response.Content.ReadAsStringAsync(), "TempFileId");
        _files.AddRange(Directory.GetFiles(TempDir(factory), id + ".*"));
        return (response, id);
    }

    private static async Task<int> LiveUploadsAsync(PlannitWebAppFactory factory, Actor who)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        return await db.TempUploads.CountAsync(u => u.ConsumedUtc == null);
    }

    [Fact]
    public async Task UploadCount_IsCappedPerUser_AndFreesWhenConfirmed()
    {
        var factory = NewFactory(false, ("Uploads:MaxPendingFilesPerUser", "2"));
        var a = await NewActorAsync(factory, "quota-a@example.invalid");
        var b = await NewActorAsync(factory, "quota-b@example.invalid");

        var (_, first) = await UploadAsync(factory, a);
        await UploadAsync(factory, a);
        var (third, thirdId) = await UploadAsync(factory, a);

        Assert.NotNull(first);
        Assert.Null(thirdId);
        Assert.True(third.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        Assert.Equal(2, await LiveUploadsAsync(factory, a));
        Assert.NotNull((await UploadAsync(factory, b)).Id); // another user is unaffected

        // Confirming one upload frees a slot.
        var page = await a.Client.GetStringAsync("/Transactions/Import");
        var confirm = await a.Client.PostAsync("/Transactions/ConfirmImport", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["AccountId"] = a.AccountId.ToString(), ["AccountName"] = "x", ["FileName"] = "s.csv", ["TempFileId"] = first!,
            ["DateColumn"] = "Date", ["DateFormat"] = "MM/dd/yyyy", ["DescriptionColumn"] = "Description", ["AmountColumn"] = "Amount"
        }));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(1, await LiveUploadsAsync(factory, a)); // the confirmed upload no longer counts
    }

    [Fact]
    public async Task UploadBytes_AreCappedPerUser()
    {
        var factory = NewFactory(false, ("Uploads:MaxPendingBytesPerUser", "300"));
        var a = await NewActorAsync(factory, "bytes@example.invalid");
        var padding = new string('x', 150);

        var (_, first) = await UploadAsync(factory, a, $"Date,Description,Amount\n01/15/2026,{padding},-1.00\n");
        var (second, secondId) = await UploadAsync(factory, a, $"Date,Description,Amount\n01/15/2026,{padding},-2.00\n");

        Assert.NotNull(first);
        Assert.Null(secondId);
        Assert.True(second.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        Assert.Equal(1, await LiveUploadsAsync(factory, a));
    }

    [Fact]
    public async Task ExpiredUploads_DoNotCountAgainstTheQuota()
    {
        var factory = NewFactory(false, ("Uploads:MaxPendingFilesPerUser", "1"));
        var a = await NewActorAsync(factory, "expiry-quota@example.invalid");
        var (_, id) = await UploadAsync(factory, a);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.TempUploads.IgnoreQueryFilters().SingleAsync(u => u.Id == Guid.Parse(id!))).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.NotNull((await UploadAsync(factory, a)).Id);
    }

    // ---------------------------------------------------------------------------------------
    // Claude CLI availability
    // ---------------------------------------------------------------------------------------

    private static bool CliAvailableTo(PlannitWebAppFactory factory, string userId)
    {
        // The status is detected at startup; pretend the CLI is installed on this machine.
        typeof(ClaudeCliStatus).GetProperty(nameof(ClaudeCliStatus.Available))!
            .SetValue(factory.Services.GetRequiredService<ClaudeCliStatus>(), true);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SetCurrentUser(userId);
        return scope.ServiceProvider.GetRequiredService<AiSettingsService>().ClaudeCliAvailable;
    }

    [Fact]
    public async Task ClaudeCli_IsHiddenFromEveryone_WhenRegistrationIsOpenAndNoAllowlist()
    {
        var factory = NewFactory(false, ("AllowRegistration", "true"));
        var a = await NewActorAsync(factory, "cli-open@example.invalid");

        Assert.False(CliAvailableTo(factory, a.UserId));

        // ...and it cannot be selected by posting the provider directly.
        var page = await a.Client.GetStringAsync("/Settings/Ai");
        var html = await (await a.Client.PostAsync("/Settings/SaveAi", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["Provider"] = nameof(AiProvider.ClaudeCli)
        }))).Content.ReadAsStringAsync();
        Assert.Contains("not available", html);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(a.UserId);
        Assert.False(await db.AiSettings.AnyAsync());
    }

    [Fact]
    public async Task ClaudeCli_IsOfferedOnlyToAllowlistedUsers()
    {
        var factory = NewFactory(false, ("AllowRegistration", "true"), ("Ai:ClaudeCli:AllowedUsers:0", "Owner@Example.invalid"));
        var owner = await NewActorAsync(factory, "owner@example.invalid");
        var other = await NewActorAsync(factory, "someone@example.invalid");

        Assert.True(CliAvailableTo(factory, owner.UserId));
        Assert.False(CliAvailableTo(factory, other.UserId));
    }

    [Fact]
    public async Task ClaudeCli_IsOfferedToTheOwner_WhenRegistrationIsClosed()
    {
        var factory = NewFactory(false, ("AllowRegistration", "false"));
        // Registration is closed, so create the user directly.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            await users.CreateAsync(new IdentityUser { UserName = "solo@example.invalid", Email = "solo@example.invalid", EmailConfirmed = true }, HttpTestHelpers.TestPassword);
        }
        string id;
        using (var scope = factory.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync("solo@example.invalid"))!.Id;

        Assert.True(CliAvailableTo(factory, id));
    }

    // ---------------------------------------------------------------------------------------
    // Projection workload bounds
    // ---------------------------------------------------------------------------------------

    private static async Task SeedScenariosAsync(PlannitWebAppFactory factory, Actor who, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        for (var i = 0; i < count; i++)
            db.ProjectionScenarios.Add(new ProjectionScenario
            {
                UserId = who.UserId, Name = $"Scenario {i:D2}", BirthYear = 1990, RetirementAge = 65, LifeExpectancy = 90,
                AnnualRetirementSpending = 40000m, InflationRate = 0.03m
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Compare_SimulatesAtMostSixScenarios_AndSaysSo()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "compare@example.invalid");
        await SeedScenariosAsync(factory, a, 9);

        var html = await a.Client.GetStringAsync("/Projections/Compare");

        Assert.Contains("Showing the first 6 of 9 scenarios", html);
        Assert.Equal(6, Regex.Matches(html, "Scenario \\d\\d").Count);
    }

    [Fact]
    public async Task Compare_ShowsEverything_WhenWithinTheLimit()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "compare-small@example.invalid");
        await SeedScenariosAsync(factory, a, 3);

        var html = await a.Client.GetStringAsync("/Projections/Compare");

        Assert.DoesNotContain("Showing the first", html);
        Assert.Equal(3, Regex.Matches(html, "Scenario \\d\\d").Count);
    }

    [Fact]
    public async Task ScenarioCount_IsCappedPerUser()
    {
        var factory = NewFactory();
        var a = await NewActorAsync(factory, "scenario-cap@example.invalid");
        await SeedScenariosAsync(factory, a, Plannit.Controllers.ProjectionsController.MaxScenariosPerUser);

        var page = await a.Client.GetStringAsync("/Projections/Create");
        var response = await a.Client.PostAsync("/Projections/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["Name"] = "One too many", ["BirthYear"] = "1990", ["RetirementAge"] = "65", ["LifeExpectancy"] = "90",
            ["AnnualRetirementSpending"] = "40000", ["InflationRate"] = "0.03", ["ReturnStdDev"] = "0.15"
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form redisplayed, not a redirect to Results
        Assert.Contains("at most 25 scenarios", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(a.UserId);
        Assert.Equal(25, await db.ProjectionScenarios.CountAsync());
    }
}
