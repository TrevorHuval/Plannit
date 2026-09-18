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

public class AuthenticationPathClassificationTests
{
    [Theory]
    [InlineData("/Identity/Account/Login")]
    [InlineData("/identity/account/login")]
    [InlineData("/Identity/Account/LoginWith2fa")]
    [InlineData("/Identity/Account/LoginWithRecoveryCode")]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/Identity/Account/RegisterConfirmation")]
    [InlineData("/Identity/Account/ConfirmEmail")]
    [InlineData("/Identity/Account/ConfirmEmailChange")]
    [InlineData("/Identity/Account/ForgotPassword")]
    [InlineData("/Identity/Account/ResetPassword")]
    [InlineData("/Identity/Account/ResendEmailConfirmation")]
    [InlineData("/Identity/Account/ExternalLogin")]
    [InlineData("/Identity/Account/Lockout")]
    public void CredentialAndTokenPages_AreAuthenticationPaths(string path) =>
        Assert.True(RateLimiterConfiguration.IsAuthenticationPath(path));

    [Theory]
    [InlineData("/Identity/Account/Manage")]
    [InlineData("/Identity/Account/Manage/Index")]
    [InlineData("/Identity/Account/Manage/EnableAuthenticator")]
    [InlineData("/Identity/Account/Logout")]
    [InlineData("/Dashboard")]
    [InlineData("/Transactions/Import")]
    [InlineData("/healthz")]
    public void SignedInAndOrdinaryPages_UseTheGlobalBudget(string path) =>
        Assert.False(RateLimiterConfiguration.IsAuthenticationPath(path));

    [Theory]
    [InlineData("/Identity/Account/LoginWith2fa")]
    [InlineData("/Identity/Account/LoginWithRecoveryCode")]
    [InlineData("/Identity/Account/ResendEmailConfirmation")]
    [InlineData("/Identity/Account/ResetPassword")]
    [InlineData("/IDENTITY/ACCOUNT/LOGIN")]
    public async Task SecondaryAuthRoutes_RejectAfterTenRequests(string path)
    {
        using var limiter = RateLimiterConfiguration.CreateGlobalLimiter();
        var ip = "203.0.113.20";

        for (var i = 0; i < RateLimiterConfiguration.AuthPermitLimit; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            using var lease = await limiter.AcquireAsync(context);
            Assert.True(lease.IsAcquired, $"Request {i + 1} should have been permitted.");
        }

        var over = new DefaultHttpContext();
        over.Request.Path = path;
        over.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        using var rejected = await limiter.AcquireAsync(over);
        Assert.False(rejected.IsAcquired);
    }
}
