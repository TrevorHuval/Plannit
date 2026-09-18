using Microsoft.Extensions.Configuration;
using Plannit.Services;

namespace Plannit.Tests;

/// <summary>A provider is registered only when every setting it needs is present.</summary>
public class ExternalLoginProvidersTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Google_RequiresIdAndSecret()
    {
        Assert.False(ExternalLoginProviders.IsGoogleConfigured(Config()));
        Assert.False(ExternalLoginProviders.IsGoogleConfigured(Config(("Authentication:Google:ClientId", "id"))));
        Assert.False(ExternalLoginProviders.IsGoogleConfigured(Config(("Authentication:Google:ClientId", "id"), ("Authentication:Google:ClientSecret", " "))));
        Assert.True(ExternalLoginProviders.IsGoogleConfigured(Config(("Authentication:Google:ClientId", "id"), ("Authentication:Google:ClientSecret", "s"))));
    }

    [Fact]
    public void Apple_RequiresIdsAndAKeySource()
    {
        var baseline = new[] { ("Authentication:Apple:ClientId", "svc"), ("Authentication:Apple:TeamId", "team"), ("Authentication:Apple:KeyId", "key") };
        Assert.False(ExternalLoginProviders.IsAppleConfigured(Config(baseline)));
        Assert.True(ExternalLoginProviders.IsAppleConfigured(Config([.. baseline, ("Authentication:Apple:PrivateKey", "-----BEGIN PRIVATE KEY-----")])));
        Assert.True(ExternalLoginProviders.IsAppleConfigured(Config([.. baseline, ("Authentication:Apple:PrivateKeyPath", "/data/AuthKey.p8")])));
        Assert.False(ExternalLoginProviders.IsAppleConfigured(Config([.. baseline[..2], ("Authentication:Apple:PrivateKey", "k")])));
    }

    [Theory]
    [InlineData("Google", "bi-google")]
    [InlineData("Apple", "bi-apple")]
    [InlineData("Something", "bi-box-arrow-in-right")]
    public void IconClass_MapsKnownProviders(string provider, string expected) =>
        Assert.Equal(expected, ExternalLoginProviders.IconClass(provider));
}
