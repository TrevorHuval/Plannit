using Plannit.Controllers;

namespace Plannit.Tests;

public class SearchTextSanitizerTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]        // angle brackets / markup
    [InlineData("\"onmouseover=alert(1)")]           // double quote
    [InlineData("javascript:alert(1)")]              // colon
    [InlineData("a\\b")]                              // backslash
    [InlineData("()")]                               // parens
    [InlineData("  ")]                               // whitespace only
    public void SanitizeSearchText_RejectsUnsafeInput_ReturnsNull(string input)
    {
        Assert.Null(TransactionsController.SanitizeSearchText(input));
    }

    [Theory]
    [InlineData("Whole Foods Market", "Whole Foods Market")]
    [InlineData("AT&T *Wireless", "AT&T *Wireless")]
    [InlineData("Trader Joe's #123", "Trader Joe's #123")]
    [InlineData("  Netflix.com  ", "Netflix.com")]   // trimmed
    [InlineData("$100 / month", "$100 / month")]
    public void SanitizeSearchText_AcceptsSafeMerchantText(string input, string expected)
    {
        Assert.Equal(expected, TransactionsController.SanitizeSearchText(input));
    }

    [Fact]
    public void SanitizeSearchText_RejectsOverLongInput()
    {
        Assert.Null(TransactionsController.SanitizeSearchText(new string('a', 101)));
    }

    [Fact]
    public void SanitizeSearchText_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(TransactionsController.SanitizeSearchText(null));
        Assert.Null(TransactionsController.SanitizeSearchText(""));
    }
}
