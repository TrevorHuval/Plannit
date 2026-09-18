using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plannit.Tests.Integration;

/// <summary>
/// A stand-in for Google/Apple: a remote authentication scheme that never leaves the test server.
/// The challenge redirects straight to its own callback path, and the callback issues a fixed
/// identity (subject + optional email + verification flag), so the ExternalLogin page's
/// enrolment policy can be exercised end-to-end over HTTP.
/// </summary>
public sealed class FakeExternalOptions : RemoteAuthenticationOptions
{
    public FakeExternalOptions()
    {
        CallbackPath = "/signin-fake";
        // The real providers mark the correlation cookie Secure (required with SameSite=None); the
        // in-process test client speaks plain http, so let it follow the request scheme instead.
        CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    }

    public string Subject { get; set; } = "fake-subject-1";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public ISecureDataFormat<AuthenticationProperties>? StateDataFormat { get; set; }
}

public sealed class FakeExternalHandler(IOptionsMonitor<FakeExternalOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : RemoteAuthenticationHandler<FakeExternalOptions>(options, logger, encoder)
{
    public const string SchemeName = "Fake";
    public const string DisplayName = "Fake Provider";

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (string.IsNullOrEmpty(properties.RedirectUri))
            properties.RedirectUri = OriginalPathBase + OriginalPath + Request.QueryString;

        GenerateCorrelationId(properties);
        var state = Options.StateDataFormat!.Protect(properties);
        Response.Redirect(BuildRedirectUri(Options.CallbackPath) + "?state=" + Uri.EscapeDataString(state));
        return Task.CompletedTask;
    }

    protected override Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        var properties = Options.StateDataFormat!.Unprotect(Request.Query["state"]);
        if (properties is null)
            return Task.FromResult(HandleRequestResult.Fail("Invalid state."));
        if (!ValidateCorrelationId(properties))
            return Task.FromResult(HandleRequestResult.Fail("Correlation failed."));

        var identity = new ClaimsIdentity(Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, Options.Subject));
        if (Options.Email is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, Options.Email));
            identity.AddClaim(new Claim("email_verified", Options.EmailVerified ? "true" : "false"));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name);
        return Task.FromResult(HandleRequestResult.Success(ticket));
    }
}

public static class FakeExternalLoginExtensions
{
    public static IServiceCollection AddFakeExternalLogin(this IServiceCollection services, Action<FakeExternalOptions> configure)
    {
        services.AddAuthentication()
            .AddRemoteScheme<FakeExternalOptions, FakeExternalHandler>(FakeExternalHandler.SchemeName, FakeExternalHandler.DisplayName, configure);
        services.AddOptions<FakeExternalOptions>(FakeExternalHandler.SchemeName)
            .PostConfigure<IDataProtectionProvider>((o, dp) =>
                o.StateDataFormat ??= new PropertiesDataFormat(dp.CreateProtector(nameof(FakeExternalHandler), FakeExternalHandler.SchemeName)));
        return services;
    }
}
