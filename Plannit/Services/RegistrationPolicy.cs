using Microsoft.Extensions.Configuration;

namespace Plannit.Services;

/// <summary>
/// Single definition of the self-service enrollment and lockout policy, read from configuration.
/// Every default fails closed:
/// <list type="bullet">
/// <item><c>AllowRegistration</c> — absent means disabled (personal deployment).</item>
/// <item><c>Identity:RequireConfirmedAccount</c> — absent means "same as AllowRegistration":
/// an open signup form requires mailbox verification; a closed one leaves existing accounts
/// (which may predate email confirmation) able to sign in.</item>
/// <item>Registration is refused outright when verification is required but no mail sender is
/// configured, so a misconfigured public instance can never mint unverifiable accounts.</item>
/// </list>
/// Used by the request-gating middleware in Program.cs and by the views that show signup links.
/// </summary>
public static class RegistrationPolicy
{
    public const int DefaultMaxFailedAccessAttempts = 5;
    public const int DefaultLockoutMinutes = 15;

    public static bool IsRegistrationEnabled(IConfiguration config) =>
        config.GetValue("AllowRegistration", false);

    public static bool IsConfirmedAccountRequired(IConfiguration config) =>
        config.GetValue("Identity:RequireConfirmedAccount", IsRegistrationEnabled(config));

    public static int MaxFailedAccessAttempts(IConfiguration config) =>
        Math.Max(1, config.GetValue("Identity:Lockout:MaxFailedAccessAttempts", DefaultMaxFailedAccessAttempts));

    public static TimeSpan LockoutDuration(IConfiguration config) =>
        TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Identity:Lockout:DurationMinutes", DefaultLockoutMinutes)));

    /// <summary>
    /// True when the Register pages may be served. <paramref name="reason"/> is a short,
    /// non-sensitive message suitable for the response body when the answer is false.
    /// </summary>
    public static bool CanRegister(IConfiguration config, IEmailSender emailSender, out string reason)
    {
        if (!IsRegistrationEnabled(config))
        {
            reason = "Registration is disabled.";
            return false;
        }

        if (IsConfirmedAccountRequired(config) && !emailSender.IsConfigured)
        {
            reason = "Registration is unavailable: this server cannot send verification email.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Whether public pages should render a link to the Register page.</summary>
    public static bool ShowRegistrationLinks(IConfiguration config, IEmailSender emailSender) =>
        CanRegister(config, emailSender, out _);

    /// <summary>
    /// The self-service enrollment pages: Register and its RegisterConfirmation follow-up. Segment
    /// matching is used (so "/Identity/Account/Registered-x" would not match) and it is
    /// case-insensitive; callers pass the PathBase-stripped request path.
    /// </summary>
    public static bool IsRegistrationPath(Microsoft.AspNetCore.Http.PathString path) =>
        path.StartsWithSegments("/Identity/Account/Register") ||
        path.StartsWithSegments("/Identity/Account/RegisterConfirmation");
}
