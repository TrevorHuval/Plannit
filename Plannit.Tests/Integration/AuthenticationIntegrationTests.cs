using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Services;

namespace Plannit.Tests.Integration;

/// <summary>
/// Secure-behaviour regression tests for the authentication findings of the public-hosting
/// audit: registration exposure, P1-02 (lockout + auth route limits) and P1-03 (Identity email).
/// Every test runs under a /plannit PathBase, as the public deployment does, unless noted.
/// </summary>
public class AuthenticationIntegrationTests
{
    private const string PathBase = "/plannit";

    private static PlannitWebAppFactory NewFactory(bool allowRegistration = true, bool? requireConfirmed = null, bool emailConfigured = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["PathBase"] = "plannit",
            ["AllowRegistration"] = allowRegistration ? "true" : "false",
        };
        // null → let the app's own default (same as AllowRegistration) apply.
        settings["Identity:RequireConfirmedAccount"] = requireConfirmed switch
        {
            true => "true",
            false => "false",
            null => null
        };
        return new PlannitWebAppFactory { Settings = settings, EmailConfigured = emailConfigured };
    }

    private static async Task<IdentityUser> CreateUserAsync(PlannitWebAppFactory factory, string email, bool emailConfirmed = false)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed };
        var result = await users.CreateAsync(user, HttpTestHelpers.TestPassword);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<IdentityUser> FindUserAsync(PlannitWebAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!;
    }

    private static async Task<(int Failed, bool LockedOut)> LockoutStateAsync(PlannitWebAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        return (await users.GetAccessFailedCountAsync(user), await users.IsLockedOutAsync(user));
    }

    private static void AssertRedirectsTo(HttpResponseMessage response, string pathFragment)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected a redirect, got {(int)response.StatusCode}.");
        Assert.Contains(pathFragment, response.Headers.Location!.ToString());
    }

    // ---------------------------------------------------------------------------------------
    // Registration exposure (personal mode fails closed; public mode requires verification)
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/identity/account/register")]
    [InlineData("/Identity/Account/RegisterConfirmation?email=x@example.invalid")]
    public async Task RegistrationDisabled_GetIsForbidden_UnderPathBase(string path)
    {
        using var factory = NewFactory(allowRegistration: false);
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync(PathBase + path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("__RequestVerificationToken", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RegistrationDisabled_ValidTokenPostIsForbidden_AndCreatesNoAccount()
    {
        // A token minted while registration was open (or on any other form) must not unlock the
        // handler: the gate runs before the page, regardless of the antiforgery cookie/token pair.
        using var open = NewFactory(allowRegistration: true, requireConfirmed: false);
        using var openClient = open.CreateClientNoRedirect();
        var token = HttpTestHelpers.ExtractAntiforgeryToken(await openClient.GetStringAsync(PathBase + "/Identity/Account/Register"));

        using var factory = NewFactory(allowRegistration: false);
        using var client = factory.CreateClientNoRedirect();
        const string email = "closed-register@example.invalid";
        var response = await client.PostAsync(PathBase + "/Identity/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = HttpTestHelpers.TestPassword,
            ["Input.ConfirmPassword"] = HttpTestHelpers.TestPassword,
            ["__RequestVerificationToken"] = token
        }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task RegistrationSetting_Absent_DefaultsToDisabled()
    {
        using var factory = new PlannitWebAppFactory { Settings = { ["AllowRegistration"] = null } };
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync("/Identity/Account/Register");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationDisabled_LoginPageAndSidebar_HideRegisterLinks()
    {
        using var factory = NewFactory(allowRegistration: false);
        using var client = factory.CreateClientNoRedirect();

        var login = await client.GetStringAsync(PathBase + "/Identity/Account/Login");
        var home = await client.GetStringAsync(PathBase + "/Home/Privacy");

        Assert.DoesNotContain("/Account/Register", login);
        Assert.DoesNotContain("/Account/Register", home);
        Assert.Contains("/Account/Login", home);
    }

    [Fact]
    public async Task RegistrationEnabled_ShowsRegisterLinks_UnderPathBase()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: false);
        using var client = factory.CreateClientNoRedirect();

        var login = await client.GetStringAsync(PathBase + "/Identity/Account/Login");

        Assert.Contains("/plannit/Identity/Account/Register", login);
    }

    [Fact]
    public async Task PublicMode_WithoutMailSender_RefusesRegistration()
    {
        // Open signup + mandatory verification + no way to send mail would strand every new
        // account, so the gate refuses rather than minting unverifiable users.
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: null, emailConfigured: false);
        using var client = factory.CreateClientNoRedirect();

        var get = await client.GetAsync(PathBase + "/Identity/Account/Register");
        var login = await client.GetStringAsync(PathBase + "/Identity/Account/Login");

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.DoesNotContain("/Account/Register", login);
    }

    [Fact]
    public async Task PublicMode_DefaultsToRequiringConfirmedAccount()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: null);
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value;

        Assert.True(options.SignIn.RequireConfirmedAccount);
    }

    // ---------------------------------------------------------------------------------------
    // P1-02 — account lockout
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void LockoutOptions_MatchConfiguredPolicy()
    {
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value;

        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.Equal(RegistrationPolicy.DefaultMaxFailedAccessAttempts, options.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(RegistrationPolicy.DefaultLockoutMinutes), options.Lockout.DefaultLockoutTimeSpan);
    }

    [Fact]
    public async Task WrongPasswords_CountTowardsLockout_AndLockTheAccount()
    {
        using var factory = NewFactory(requireConfirmed: false);
        const string email = "lockout-count@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        for (var i = 1; i < RegistrationPolicy.DefaultMaxFailedAccessAttempts; i++)
        {
            var failed = await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!", PathBase);
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode); // form redisplayed
            Assert.Equal((i, false), await LockoutStateAsync(factory, email));
        }

        var locking = await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!", PathBase);

        AssertRedirectsTo(locking, "/plannit/Identity/Account/Lockout");
        var (_, lockedOut) = await LockoutStateAsync(factory, email);
        Assert.True(lockedOut);
    }

    [Fact]
    public async Task CorrectPassword_CannotBypassActiveLockout()
    {
        using var factory = NewFactory(requireConfirmed: false);
        const string email = "lockout-bypass@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        for (var i = 0; i < RegistrationPolicy.DefaultMaxFailedAccessAttempts; i++)
            await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!", PathBase);

        var withRealPassword = await HttpTestHelpers.PostLoginAsync(client, email, HttpTestHelpers.TestPassword, PathBase);

        AssertRedirectsTo(withRealPassword, "/plannit/Identity/Account/Lockout");
        var home = await client.GetAsync(PathBase + "/Transactions");
        AssertRedirectsTo(home, "/plannit/Identity/Account/Login"); // still anonymous
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailedAttemptCounter()
    {
        using var factory = NewFactory(requireConfirmed: false);
        const string email = "lockout-reset@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!", PathBase);
        await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!", PathBase);
        Assert.Equal((2, false), await LockoutStateAsync(factory, email));

        var success = await HttpTestHelpers.PostLoginAsync(client, email, HttpTestHelpers.TestPassword, PathBase);

        AssertRedirectsTo(success, "/plannit/");
        Assert.Equal((0, false), await LockoutStateAsync(factory, email));
    }

    [Fact]
    public async Task LockoutThreshold_IsConfigurable()
    {
        using var factory = new PlannitWebAppFactory
        {
            Settings = { ["Identity:Lockout:MaxFailedAccessAttempts"] = "2", ["Identity:Lockout:DurationMinutes"] = "30" }
        };
        const string email = "lockout-config@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!");
        Assert.Equal((1, false), await LockoutStateAsync(factory, email));
        var second = await HttpTestHelpers.PostLoginAsync(client, email, "WrongPassword1!");

        AssertRedirectsTo(second, "/Identity/Account/Lockout");
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value;
        Assert.Equal(TimeSpan.FromMinutes(30), options.Lockout.DefaultLockoutTimeSpan);
    }

    // ---------------------------------------------------------------------------------------
    // P1-02 — rate limiting of the whole authentication surface through the real pipeline
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Identity/Account/LoginWith2fa")]
    [InlineData("/Identity/Account/LoginWithRecoveryCode")]
    [InlineData("/Identity/Account/ResendEmailConfirmation")]
    [InlineData("/Identity/Account/ForgotPassword")]
    [InlineData("/Identity/Account/ResetPassword?code=x")]
    [InlineData("/identity/account/login")]
    public async Task AuthRoutes_ShareTheTightAuthBudget_UnderPathBase(string path)
    {
        using var factory = new PlannitWebAppFactory { DisableRateLimiter = false, Settings = { ["PathBase"] = "plannit" } };
        using var client = factory.CreateClientNoRedirect();

        for (var i = 0; i < RateLimiterConfiguration.AuthPermitLimit; i++)
        {
            var ok = await client.GetAsync(PathBase + path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }

        var over = await client.GetAsync(PathBase + path);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // P1-03 — Identity email wiring and confirmation/reset flows
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IdentityEmailSender_ResolvesToRealAdapter_NotNoOp()
    {
        using var factory = NewFactory();
        using var scope = factory.Services.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender>();

        Assert.IsType<IdentityEmailSender>(sender);
    }

    
    [Fact]
    public async Task Register_SendsConfirmationLink_ToSink_WithPathBase_AndLoginIsBlockedUntilConfirmed()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        using var client = factory.CreateClientNoRedirect();
        const string email = "confirm-flow@example.invalid";

        var registered = await HttpTestHelpers.PostRegisterAsync(client, email, pathBase: PathBase);
        AssertRedirectsTo(registered, "/plannit/Identity/Account/RegisterConfirmation");

        var mail = Assert.Single(factory.Emails.Sent);
        Assert.Equal(email, mail.To);
        Assert.True(mail.IsHtml);
        var link = CapturingEmailSender.ExtractLink(mail);
        Assert.Contains("/plannit/Identity/Account/ConfirmEmail", link);
        Assert.Contains("code=", link);

        // Unconfirmed: password is right but sign-in is refused, and no lockout counter is charged.
        var early = await HttpTestHelpers.PostLoginAsync(client, email, HttpTestHelpers.TestPassword, PathBase);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.False((await FindUserAsync(factory, email)).EmailConfirmed);

        var confirmed = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True((await FindUserAsync(factory, email)).EmailConfirmed);

        var login = await HttpTestHelpers.PostLoginAsync(client, email, HttpTestHelpers.TestPassword, PathBase);
        AssertRedirectsTo(login, "/plannit/");
    }

    [Fact]
    public async Task ConfirmationLink_UsesForwardedHttpsScheme()
    {
        using var factory = new PlannitWebAppFactory
        {
            Settings =
            {
                ["PathBase"] = "plannit",
                ["Identity:RequireConfirmedAccount"] = "true",
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:TrustProxyNetwork"] = "true"
            }
        };
        using var client = factory.CreateClientNoRedirect();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        await HttpTestHelpers.PostRegisterAsync(client, "https-link@example.invalid", pathBase: PathBase);

        var link = CapturingEmailSender.ExtractLink(Assert.Single(factory.Emails.Sent));
        Assert.StartsWith("https://localhost/plannit/Identity/Account/ConfirmEmail", link);
    }

    [Fact]
    public async Task RegisterConfirmationPage_ExposesNoTokenOrLink_AndCannotConfirm()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        const string email = "no-inline-link@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        var response = await client.GetAsync(PathBase + "/Identity/Account/RegisterConfirmation?email=" + Uri.EscapeDataString(email));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("confirm-link", html);
        Assert.DoesNotContain("ConfirmEmail", html);
        Assert.DoesNotContain("code=", html);
        Assert.False((await FindUserAsync(factory, email)).EmailConfirmed);
        Assert.Empty(factory.Emails.Sent);
    }

    [Fact]
    public async Task RegisterConfirmationPage_DoesNotRevealWhetherAccountExists()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        await CreateUserAsync(factory, "exists@example.invalid");
        using var client = factory.CreateClientNoRedirect();

        var existing = await client.GetAsync(PathBase + "/Identity/Account/RegisterConfirmation?email=exists%40example.invalid");
        var missing = await client.GetAsync(PathBase + "/Identity/Account/RegisterConfirmation?email=missing%40example.invalid");

        Assert.Equal(existing.StatusCode, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_WithInvalidToken_DoesNotConfirm()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        const string email = "bad-token@example.invalid";
        var user = await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();
        var bogus = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("not-a-real-token"));

        var response = await client.GetAsync($"{PathBase}/Identity/Account/ConfirmEmail?userId={user.Id}&code={bogus}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Error confirming your email", await response.Content.ReadAsStringAsync());
        Assert.False((await FindUserAsync(factory, email)).EmailConfirmed);
    }

    [Fact]
    public async Task ExistingUnconfirmedAccount_CanResendConfirmation_AndConfirm()
    {
        // Accounts created before mail was wired (or whose first message was lost) recover via Resend.
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        const string email = "legacy-unconfirmed@example.invalid";
        await CreateUserAsync(factory, email);
        using var client = factory.CreateClientNoRedirect();

        var page = await client.GetStringAsync(PathBase + "/Identity/Account/ResendEmailConfirmation");
        var resend = await client.PostAsync(PathBase + "/Identity/Account/ResendEmailConfirmation", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        var link = CapturingEmailSender.ExtractLink(Assert.Single(factory.Emails.Sent));
        Assert.Contains("/plannit/Identity/Account/ConfirmEmail", link);
        await client.GetAsync(link);

        Assert.True((await FindUserAsync(factory, email)).EmailConfirmed);
    }

    [Fact]
    public async Task ResendConfirmation_ForUnknownAddress_SendsNothing_AndLooksIdentical()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true);
        using var client = factory.CreateClientNoRedirect();

        var page = await client.GetStringAsync(PathBase + "/Identity/Account/ResendEmailConfirmation");
        var resend = await client.PostAsync(PathBase + "/Identity/Account/ResendEmailConfirmation", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = "nobody@example.invalid",
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));

        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.Empty(factory.Emails.Sent);
    }

    [Fact]
    public async Task ForgotPassword_SendsResetLink_ToSink_AndResetWorks_UnderPathBase()
    {
        using var factory = NewFactory(requireConfirmed: false);
        const string email = "reset-flow@example.invalid";
        await CreateUserAsync(factory, email, emailConfirmed: true);
        using var client = factory.CreateClientNoRedirect();

        var forgotPage = await client.GetStringAsync(PathBase + "/Identity/Account/ForgotPassword");
        var forgot = await client.PostAsync(PathBase + "/Identity/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(forgotPage)
        }));
        AssertRedirectsTo(forgot, "/plannit/Identity/Account/ForgotPasswordConfirmation");

        var mail = Assert.Single(factory.Emails.Sent);
        var link = CapturingEmailSender.ExtractLink(mail);
        Assert.Contains("/plannit/Identity/Account/ResetPassword", link);

        var resetPage = await client.GetStringAsync(link);
        const string newPassword = "BrandNewPassword2!";
        var reset = await client.PostAsync(PathBase + "/Identity/Account/ResetPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
            ["Input.Code"] = HttpTestHelpers.ExtractHiddenField(resetPage, "Input.Code"),
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(resetPage)
        }));
        AssertRedirectsTo(reset, "/plannit/Identity/Account/ResetPasswordConfirmation");

        var oldLogin = await HttpTestHelpers.PostLoginAsync(client, email, HttpTestHelpers.TestPassword, PathBase);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        var newLogin = await HttpTestHelpers.PostLoginAsync(client, email, newPassword, PathBase);
        AssertRedirectsTo(newLogin, "/plannit/");
    }

    [Fact]
    public async Task ForgotPassword_ForUnknownAddress_SendsNothing()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClientNoRedirect();

        var page = await client.GetStringAsync(PathBase + "/Identity/Account/ForgotPassword");
        var forgot = await client.PostAsync(PathBase + "/Identity/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = "nobody@example.invalid",
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page)
        }));

        AssertRedirectsTo(forgot, "/plannit/Identity/Account/ForgotPasswordConfirmation");
        Assert.Empty(factory.Emails.Sent);
    }
}
