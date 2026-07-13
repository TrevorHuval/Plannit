using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Plannit.Services;

public static class RateLimiterConfiguration
{
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter()
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var path = httpContext.Request.Path;

            if (path.StartsWithSegments("/Identity/Account/Login") ||
                path.StartsWithSegments("/Identity/Account/Register") ||
                path.StartsWithSegments("/Identity/Account/ForgotPassword"))
            {
                return RateLimitPartition.GetFixedWindowLimiter($"auth:{ip}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            }

            return RateLimitPartition.GetFixedWindowLimiter($"global:{ip}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });
    }
}
