using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Plannit.Services;

public static class RateLimiterConfiguration
{
    /// <summary>Per-IP budget for the credential-bearing Identity pages.</summary>
    public const int AuthPermitLimit = 10;
    /// <summary>Per-IP budget for everything else.</summary>
    public const int GlobalPermitLimit = 300;
    /// <summary>Requests per minute, per signed-in user (per IP when anonymous), to expensive endpoints.</summary>
    public const int ExpensivePermitLimit = 20;
    /// <summary>Expensive requests one user may have running at the same time.</summary>
    public const int ExpensiveConcurrency = 2;

    private static readonly string[] ExpensivePostPaths =
    [
        "/Transactions/Import", "/Transactions/ConfirmImport", "/Transactions/ConfirmSnapshotImport",
        "/SmartCategorize",
        "/Settings/TestConnection", "/Settings/SendTestEmail", "/Settings/SendVerificationEmail",
        "/Sync/Connect", "/Sync/Refresh", "/Sync/SyncNow"
    ];

    private static readonly string[] ExpensiveAnyPaths = ["/Projections/Results", "/Projections/Compare"];

    /// <summary>
    /// Endpoints that spend real CPU, disk or upstream quota: statement parsing, AI calls, bank sync,
    /// outbound mail and Monte Carlo projections. They get their own tighter per-user budget and a
    /// concurrency cap instead of sharing the broad limit used for ordinary navigation (audit P2-08).
    /// </summary>
    public static bool IsExpensiveRequest(PathString path, string method) =>
        ExpensiveAnyPaths.Any(p => path.StartsWithSegments(p)) ||
        (HttpMethods.IsPost(method) && ExpensivePostPaths.Any(p => path.StartsWithSegments(p)));

    /// <summary>The signed-in user's id when known, otherwise the client IP, so a shared IP does not pool users.</summary>
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? $"user:{id}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

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
        // Two limiters are chained: a request must satisfy the rate budget for its class AND, for
        // expensive endpoints, the per-user concurrency cap (held until the response completes).
        return PartitionedRateLimiter.CreateChained(CreateRateLimiter(), CreateConcurrencyLimiter());
    }

    private static PartitionedRateLimiter<HttpContext> CreateConcurrencyLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            IsExpensiveRequest(httpContext.Request.Path, httpContext.Request.Method)
                ? RateLimitPartition.GetConcurrencyLimiter($"concurrency:{PartitionKey(httpContext)}", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = ExpensiveConcurrency,
                    QueueLimit = 0
                })
                : RateLimitPartition.GetNoLimiter("not-expensive"));

    private static PartitionedRateLimiter<HttpContext> CreateRateLimiter()
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (IsExpensiveRequest(httpContext.Request.Path, httpContext.Request.Method))
            {
                return RateLimitPartition.GetFixedWindowLimiter($"expensive:{PartitionKey(httpContext)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ExpensivePermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            }

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
