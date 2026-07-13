using Microsoft.AspNetCore.Http;
using Plannit.Services;

namespace Plannit.Tests;

public class RateLimiterConfigurationTests
{
    private static DefaultHttpContext MakeContext(string path, string ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        return context;
    }

    [Fact]
    public async Task AuthPath_RejectsAfterTenRequests_FromSameIp()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();

        for (var i = 0; i < 10; i++)
        {
            using var lease = await limiter.AcquireAsync(MakeContext("/Identity/Account/Login"));
            Assert.True(lease.IsAcquired, $"Request {i + 1} should have been permitted.");
        }

        using var eleventh = await limiter.AcquireAsync(MakeContext("/Identity/Account/Login"));
        Assert.False(eleventh.IsAcquired);
    }

    [Fact]
    public async Task RegisterAndForgotPassword_ShareTheAuthBucket_WithLogin()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();
        var ip = "203.0.113.9";

        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter.AcquireAsync(MakeContext("/Identity/Account/Login", ip));
            Assert.True(lease.IsAcquired);
        }
        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter.AcquireAsync(MakeContext("/Identity/Account/Register", ip));
            Assert.True(lease.IsAcquired);
        }

        using var rejected = await limiter.AcquireAsync(MakeContext("/Identity/Account/ForgotPassword", ip));
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public async Task NonAuthPath_AllowsMoreThanTenRequests_FromSameIp()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();
        var ip = "203.0.113.8";

        for (var i = 0; i < 50; i++)
        {
            using var lease = await limiter.AcquireAsync(MakeContext("/Dashboard", ip));
            Assert.True(lease.IsAcquired, $"Request {i + 1} to a non-auth path should have been permitted.");
        }
    }

    [Fact]
    public async Task DifferentIps_AreRateLimited_Independently()
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();

        for (var i = 0; i < 10; i++)
        {
            using var lease = await limiter.AcquireAsync(MakeContext("/Identity/Account/Login", "198.51.100.1"));
            Assert.True(lease.IsAcquired);
        }

        // A different IP hitting the same auth bucket must not be affected by the first IP's usage.
        using var otherIpLease = await limiter.AcquireAsync(MakeContext("/Identity/Account/Login", "198.51.100.2"));
        Assert.True(otherIpLease.IsAcquired);
    }
}
