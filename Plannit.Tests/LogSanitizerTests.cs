using Plannit.Services;

namespace Plannit.Tests;

public class LogSanitizerTests
{
    [Fact]
    public void Clean_StripsCarriageReturnAndLineFeed()
    {
        var forged = "normal.csv\r\nFATAL admin logged in as root";
        var cleaned = LogSanitizer.Clean(forged);

        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\n', cleaned);
        Assert.Equal("normal.csvFATAL admin logged in as root", cleaned);
    }

    [Fact]
    public void Clean_RemovesOtherControlCharacters()
    {
        var cleaned = LogSanitizer.Clean("a\tb\0cd");
        Assert.Equal("abcd", cleaned);
    }

    [Fact]
    public void Clean_CapsLength()
    {
        var cleaned = LogSanitizer.Clean(new string('x', 500));
        Assert.Equal(200, cleaned.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clean_NullOrEmpty_ReturnsEmptyString(string? input)
    {
        Assert.Equal("", LogSanitizer.Clean(input));
    }

    [Theory]
    [InlineData("trevor@gmail.com", "t***@g***.com")]
    [InlineData("a@b.co", "a***@b***.co")]
    [InlineData("user@localhost", "u***@l***")]
    public void MaskEmail_MasksLocalAndDomain(string input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.MaskEmail(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("@nolocal.com")]
    [InlineData("nodomain@")]
    public void MaskEmail_InvalidInput_ReturnsRedacted(string? input)
    {
        Assert.Equal("***", LogSanitizer.MaskEmail(input));
    }
}
