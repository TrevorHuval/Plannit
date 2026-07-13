using Plannit.Controllers;

namespace Plannit.Tests;

public class CsvExportEscapingTests
{
    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1234567890", "'+1234567890")]
    [InlineData("-2+3+cmd|' /C calc'!A0", "'-2+3+cmd|' /C calc'!A0")]
    [InlineData("@WEBSERVICE(\"http://evil\")", "\"'@WEBSERVICE(\"\"http://evil\"\")\"")]
    [InlineData("Normal Store", "Normal Store")]
    [InlineData("", "")]
    public void CsvEscapeText_NeutralizesLeadingFormulaTriggers(string input, string expected)
    {
        Assert.Equal(expected, TransactionsController.CsvEscapeText(input));
    }

    [Fact]
    public void CsvEscapeText_StillQuotesCommas()
    {
        Assert.Equal("\"'=1+2,3\"", TransactionsController.CsvEscapeText("=1+2,3"));
    }
}
