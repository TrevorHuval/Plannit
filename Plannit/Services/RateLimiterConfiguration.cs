using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Plannit.Services;

public static class RateLimiterConfiguration
{
    /// <summary>Per-IP budget for the credential-bearing Identity pages.</summary>
    public const int AuthPermitLimit = 10;
    /// <summary>Per-IP budget for everything else.</summary>
    public const int GlobalPermitLimit = 300;

    /// <summary>
    /// Every page under /Identity/Account is an unauthenticated credential, token or enrollment
    /// surface (login, MFA, recovery codes, register, confirm, forgot/reset, resend) and gets the
    /// tight auth budget. The two exceptions are the signed-in account-management area and Logout,
    /// which ordinary navigation hits. Segment matching is case-insensitive and the path seen here
    /// is already PathBase-stripped, so a /plannit prefix does not change the classification.
    /// </summary>
    public static bool IsAuthenticationPath(PathString path)
    {
        if (!path.StartsWithSegments("/Identity/Account", out var remaining))
            return false;

        return !remaining.StartsWithSegments("/Manage") && !remaining.StartsWithSegments("/Logout");
    }

    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter()
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (IsAuthenticationPath(httpContext.Request.Path))
            {
                return RateLimitPartition.GetFixedWindowLimiter($"auth:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AuthPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            }

            return RateLimitPartition.GetFixedWindowLimiter($"global:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = GlobalPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });
    }
}
