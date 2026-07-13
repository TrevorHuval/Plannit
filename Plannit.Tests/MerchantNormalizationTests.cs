using Plannit.Services;

namespace Plannit.Tests;

public class MerchantNormalizationTests
{
    [Theory]
    [InlineData("Amazon.com*RC3GY2873", "AMAZON.COM")]
    [InlineData("Amazon.com*AB19KTQ4Z", "AMAZON.COM")]
    public void StripsAsteriskReferenceSuffix(string input, string expected)
    {
        Assert.Equal(expected, RecurringDetectionService.NormalizeMerchant(input));
    }

    [Fact]
    public void StripsLongDigitRuns()
    {
        Assert.Equal("TARGET STORE", RecurringDetectionService.NormalizeMerchant("TARGET STORE 4521"));
    }

    [Fact]
    public void StripsHashReferenceNumbers()
    {
        Assert.Equal("STARBUCKS", RecurringDetectionService.NormalizeMerchant("STARBUCKS #1234"));
    }

    [Fact]
    public void StripsApplePayTail()
    {
        Assert.Equal("STARBUCKS", RecurringDetectionService.NormalizeMerchant("STARBUCKS APPLE PAY ENDING IN 4477"));
    }

    [Fact]
    public void StripsTrailingCityStateWhenStateCodeIsValid()
    {
        Assert.Equal("SHELL OIL", RecurringDetectionService.NormalizeMerchant("SHELL OIL SEATTLE WA"));
    }

    [Fact]
    public void DoesNotStripTrailingTwoLetterWordThatIsNotARealStateCode()
    {
        // "GO" is not a US state code — the trailing-word heuristic must not fire on it.
        Assert.Equal("SPEEDY GO", RecurringDetectionService.NormalizeMerchant("Speedy GO"));
    }

    [Fact]
    public void CollapsesWhitespaceAndUppercases()
    {
        Assert.Equal("NETFLIX.COM", RecurringDetectionService.NormalizeMerchant("  netflix.com   "));
    }

    [Fact]
    public void PreservesShortNumbersThatArentReferenceNoise()
    {
        Assert.Equal("7-ELEVEN", RecurringDetectionService.NormalizeMerchant("7-Eleven"));
    }

    [Fact]
    public void DifferentlySuffixedChargesNormalizeToTheSameKey()
    {
        var a = RecurringDetectionService.NormalizeMerchant("Amazon.com*RC3GY2873");
        var b = RecurringDetectionService.NormalizeMerchant("Amazon.com*AB1234XY");

        Assert.Equal(a, b);
        Assert.Equal("AMAZON.COM", a);
    }

    [Fact]
    public void HandlesEmptyDescription()
    {
        Assert.Equal(string.Empty, RecurringDetectionService.NormalizeMerchant(""));
    }
}
