using System.Net;

namespace Plannit.Tests.Integration;

/// <summary>
/// Exercises the auth-path rate limiter through the full middleware pipeline. Uses its own
/// factory with the limiter ENABLED and its own private limiter partition, so it stays
/// isolated from the other integration tests (which disable the limiter).
/// </summary>
public class RateLimiterIntegrationTests : IDisposable
{
    private readonly PlannitWebAppFactory _factory = new() { DisableRateLimiter = false };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AuthPath_ReturnsTooManyRequests_AfterTenRequests()
    {
        var client = _factory.CreateClient();

        // The auth bucket permits 10 requests per minute per IP; all test traffic shares one IP.
        for (var i = 0; i < 10; i++)
        {
            var ok = await client.GetAsync("/Identity/Account/Login");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }

        var eleventh = await client.GetAsync("/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }
}
