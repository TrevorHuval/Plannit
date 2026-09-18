using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;

namespace Plannit.Tests.Integration;

/// <summary>
/// "Sign in with Google/Apple" behaviour, driven through a fake remote provider
/// (<see cref="FakeExternalHandler"/>) under the /plannit PathBase used in production.
/// The invariant under test: an external identity nobody has linked is a sign-up, and sign-up
/// obeys <c>AllowRegistration</c> exactly like the password Register page does.
/// </summary>
public class ExternalLoginIntegrationTests
{
    private const string PathBase = "/plannit";
    private const string ExternalEmail = "external.user@example.com";

    private static PlannitWebAppFactory NewFactory(bool allowRegistration, bool requireConfirmed = false,
        string? email = ExternalEmail, bool emailVerified = true, bool emailConfigured = true)
    {
        return new PlannitWebAppFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["PathBase"] = "plannit",
                ["AllowRegistration"] = allowRegistration ? "true" : "false",
                ["Identity:RequireConfirmedAccount"] = requireConfirmed ? "true" : "false",
            },
            EmailConfigured = emailConfigured,
            ConfigureTestServices = services => services.AddFakeExternalLogin(o =>
            {
                o.Email = email;
                o.EmailVerified = emailVerified;
            })
        };
    }

    /// <summary>
    /// Clicks "Continue with Fake Provider" and follows the round trip through the provider's
    /// callback, returning the response of the ExternalLogin callback page itself.
    /// </summary>
    private static async Task<HttpResponseMessage> SignInWithFakeAsync(HttpClient client)
    {
        var loginPage = await client.GetAsync($"{PathBase}/Identity/Account/Login");
        loginPage.EnsureSuccessStatusCode();
        var html = await loginPage.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{FakeExternalHandler.SchemeName}\"", html);
        var token = HttpTestHelpers.ExtractAntiforgeryToken(html);

        var challenge = await client.PostAsync($"{PathBase}/Identity/Account/ExternalLogin?returnUrl=%2Fplannit%2F",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["provider"] = FakeExternalHandler.SchemeName,
                ["__RequestVerificationToken"] = token
            }));
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var providerUrl = challenge.Headers.Location!.ToString();
        Assert.StartsWith($"http://localhost{PathBase}/signin-fake?", providerUrl);

        var providerCallback = await client.GetAsync(providerUrl);
        Assert.Equal(HttpStatusCode.Found, providerCallback.StatusCode);
        var appCallback = providerCallback.Headers.Location!.ToString();
        Assert.Contains("/Identity/Account/ExternalLogin?", appCallback);
        Assert.Contains("handler=Callback", appCallback);

        return await client.GetAsync(appCallback);
    }

    private static async Task<IdentityUser?> FindUserAsync(PlannitWebAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email);
    }

    private static async Task<int> UserCountAsync(PlannitWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.Count();
    }

    private static async Task<bool> IsSignedInAsync(HttpClient client)
    {
        var resp = await client.GetAsync($"{PathBase}/Accounts");
        return resp.StatusCode == HttpStatusCode.OK;
    }

    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LoginPage_WithoutProviders_ShowsNoProviderSectionOrStockPlaceholder()
    {
        using var factory = new PlannitWebAppFactory();
        var html = await factory.CreateClient().GetStringAsync("/Identity/Account/Login");
        Assert.DoesNotContain("pl-provider-btn", html);
        Assert.DoesNotContain("no external authentication services", html);
        Assert.Contains("Welcome back", html);
    }

    [Fact]
    public async Task LoginPage_WithProvider_RendersButtonAndDivider()
    {
        using var factory = NewFactory(allowRegistration: false);
        var html = await factory.CreateClient().GetStringAsync($"{PathBase}/Identity/Account/Login");
        Assert.Contains($"value=\"{FakeExternalHandler.SchemeName}\"", html);
        Assert.Contains($"Continue with {FakeExternalHandler.DisplayName}", html);
        Assert.Contains("or sign in with email", html);
    }

    [Fact]
    public async Task CspFormAction_AllowsRedirectToProviders()
    {
        using var factory = new PlannitWebAppFactory();
        var resp = await factory.CreateClient().GetAsync("/Identity/Account/Login");
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self' https://accounts.google.com https://appleid.apple.com", csp);
    }

    [Fact]
    public async Task RegistrationDisabled_UnknownExternalIdentity_IsRefusedAndCreatesNoUser()
    {
        using var factory = NewFactory(allowRegistration: false);
        var client = factory.CreateClientNoRedirect();

        var callback = await SignInWithFakeAsync(client);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Contains("/Identity/Account/Login", callback.Headers.Location!.ToString());

        var login = await client.GetAsync(callback.Headers.Location);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("No Plannit account is linked to this Fake Provider account", html);

        Assert.Equal(0, await UserCountAsync(factory));
        Assert.False(await IsSignedInAsync(client));
    }

    [Fact]
    public async Task RegistrationEnabled_VerifiedEmail_CreatesConfirmedUserAndSignsIn()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true, emailConfigured: false);
        var client = factory.CreateClientNoRedirect();

        var callback = await SignInWithFakeAsync(client);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var html = await callback.Content.ReadAsStringAsync();
        Assert.Contains("Verified by Fake Provider", html);
        Assert.Contains(ExternalEmail, html);
        // Verified addresses are not editable.
        Assert.DoesNotContain("name=\"Input.Email\"", html);

        var confirm = await client.PostAsync($"{PathBase}/Identity/Account/ExternalLogin?handler=Confirmation&returnUrl=%2Fplannit%2F",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                // A tampered address must be ignored in favour of the provider's.
                ["Input.Email"] = "attacker@example.com",
                ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(html)
            }));
        Assert.Equal(HttpStatusCode.Found, confirm.StatusCode);
        Assert.Equal($"{PathBase}/", confirm.Headers.Location!.ToString());

        var user = await FindUserAsync(factory, ExternalEmail);
        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.Null(await FindUserAsync(factory, "attacker@example.com"));
        Assert.Empty(factory.Emails.Sent);
        Assert.True(await IsSignedInAsync(client));
    }

    [Fact]
    public async Task RegistrationEnabled_UnverifiedEmail_RequiresMailboxConfirmation()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true, email: null);
        var client = factory.CreateClientNoRedirect();

        var callback = await SignInWithFakeAsync(client);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        var html = await callback.Content.ReadAsStringAsync();
        Assert.Contains("name=\"Input.Email\"", html);

        var confirm = await client.PostAsync($"{PathBase}/Identity/Account/ExternalLogin?handler=Confirmation&returnUrl=%2Fplannit%2F",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = ExternalEmail,
                ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(html)
            }));
        Assert.Equal(HttpStatusCode.Found, confirm.StatusCode);
        Assert.Contains("/Identity/Account/RegisterConfirmation", confirm.Headers.Location!.ToString());

        var user = await FindUserAsync(factory, ExternalEmail);
        Assert.NotNull(user);
        Assert.False(user!.EmailConfirmed);
        var mail = Assert.Single(factory.Emails.Sent);
        Assert.Equal(ExternalEmail, mail.To);
        Assert.Contains($"{PathBase}/Identity/Account/ConfirmEmail", CapturingEmailSender.ExtractLink(mail));
        Assert.False(await IsSignedInAsync(client));
    }

    [Fact]
    public async Task RegistrationEnabled_UnverifiedEmail_NoMailServer_IsRefused()
    {
        using var factory = NewFactory(allowRegistration: true, requireConfirmed: true, email: ExternalEmail, emailVerified: false, emailConfigured: false);
        var client = factory.CreateClientNoRedirect();

        var callback = await SignInWithFakeAsync(client);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Contains("/Identity/Account/Login", callback.Headers.Location!.ToString());
        Assert.Equal(0, await UserCountAsync(factory));
    }

    [Fact]
    public async Task LinkedExternalIdentity_SignsIn_EvenWhenRegistrationDisabled()
    {
        using var factory = NewFactory(allowRegistration: false);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = new IdentityUser { UserName = "owner@example.com", Email = "owner@example.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, HttpTestHelpers.TestPassword)).Succeeded);
            Assert.True((await users.AddLoginAsync(user,
                new UserLoginInfo(FakeExternalHandler.SchemeName, "fake-subject-1", FakeExternalHandler.DisplayName))).Succeeded);
        }

        var client = factory.CreateClientNoRedirect();
        var callback = await SignInWithFakeAsync(client);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal($"{PathBase}/", callback.Headers.Location!.ToString());
        Assert.True(await IsSignedInAsync(client));
        Assert.Equal(1, await UserCountAsync(factory));
    }
}
