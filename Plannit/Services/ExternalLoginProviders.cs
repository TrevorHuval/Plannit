using System.Security.Claims;
using System.Text.Json;
using AspNet.Security.OAuth.Apple;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;

namespace Plannit.Services;

/// <summary>
/// Config-driven registration of the "Sign in with Google / Apple" providers. A provider is only
/// added when every setting it needs is present, so an unconfigured instance shows no provider
/// buttons instead of failing at the redirect. Settings live under <c>Authentication:&lt;Provider&gt;</c>:
/// <list type="bullet">
/// <item>Google — <c>ClientId</c>, <c>ClientSecret</c>. Redirect URI is <c>{PathBase}/signin-google</c>.</item>
/// <item>Apple — <c>ClientId</c> (the Services ID), <c>TeamId</c>, <c>KeyId</c>, and either
/// <c>PrivateKey</c> (the .p8 contents) or <c>PrivateKeyPath</c>. Redirect URI is <c>{PathBase}/signin-apple</c>
/// and must be HTTPS; Apple posts the result back cross-site, which the default SameSite=None correlation cookie allows.</item>
/// </list>
/// Both providers assert whether they verified the account's email; <see cref="HasVerifiedEmail"/>
/// reads that so external sign-up can skip the mailbox round-trip only when the provider vouches for it.
/// </summary>
public static class ExternalLoginProviders
{
    /// <summary>Origins the login form may be redirected to; appended to the CSP <c>form-action</c> directive.</summary>
    public const string FormActionSources = "https://accounts.google.com https://appleid.apple.com";

    private const string EmailVerifiedClaim = "email_verified";

    public static bool IsGoogleConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]) &&
        !string.IsNullOrWhiteSpace(config["Authentication:Google:ClientSecret"]);

    public static bool IsAppleConfigured(IConfiguration config)
    {
        var apple = config.GetSection("Authentication:Apple");
        return !string.IsNullOrWhiteSpace(apple["ClientId"]) &&
               !string.IsNullOrWhiteSpace(apple["TeamId"]) &&
               !string.IsNullOrWhiteSpace(apple["KeyId"]) &&
               (!string.IsNullOrWhiteSpace(apple["PrivateKey"]) || !string.IsNullOrWhiteSpace(apple["PrivateKeyPath"]));
    }

    public static IServiceCollection AddPlannitExternalLogins(this IServiceCollection services, IConfiguration config)
    {
        var auth = services.AddAuthentication();

        if (IsGoogleConfigured(config))
        {
            auth.AddGoogle(options =>
            {
                options.ClientId = config["Authentication:Google:ClientId"]!;
                options.ClientSecret = config["Authentication:Google:ClientSecret"]!;
                // Google's userinfo document carries the verification flag as a boolean under
                // "email_verified" (v3) or "verified_email" (v2); surface it as a string claim.
                options.ClaimActions.MapCustomJson(EmailVerifiedClaim, user =>
                    (user.TryGetProperty("email_verified", out var v) || user.TryGetProperty("verified_email", out v))
                    && v.ValueKind == JsonValueKind.True ? "true" : "false");
            });
        }

        if (IsAppleConfigured(config))
        {
            var apple = config.GetSection("Authentication:Apple");
            var keyPath = apple["PrivateKeyPath"];
            var inlineKey = apple["PrivateKey"];

            auth.AddApple(options =>
            {
                options.ClientId = apple["ClientId"]!;
                options.TeamId = apple["TeamId"]!;
                options.KeyId = apple["KeyId"]!;
                options.GenerateClientSecret = true;
                options.PrivateKey = async (_, ct) =>
                {
                    var pem = !string.IsNullOrWhiteSpace(inlineKey)
                        ? inlineKey
                        : await File.ReadAllTextAsync(keyPath!, ct);
                    // Env vars can't hold newlines comfortably; accept a literal "\n" encoding.
                    return pem.Replace("\n", "\n").AsMemory();
                };
            });
        }

        return services;
    }

    /// <summary>Email the provider reported for this identity, or null (Apple omits it after the first authorization).</summary>
    public static string? GetEmail(ExternalLoginInfo info) =>
        info.Principal.FindFirstValue(ClaimTypes.Email) ?? info.Principal.FindFirstValue("email");

    /// <summary>
    /// True when the provider states it verified the email address. Apple sends the raw id_token
    /// claim (a "true" string or boolean); Google's is mapped above. Anything else is treated as unverified.
    /// </summary>
    public static bool HasVerifiedEmail(ExternalLoginInfo info) =>
        GetEmail(info) is not null &&
        string.Equals(info.Principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bootstrap Icons glyph for a provider button.</summary>
    public static string IconClass(string provider) => provider switch
    {
        GoogleDefaults.AuthenticationScheme => "bi-google",
        AppleAuthenticationDefaults.AuthenticationScheme => "bi-apple",
        _ => "bi-box-arrow-in-right"
    };
}
