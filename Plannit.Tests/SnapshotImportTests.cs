using System.Text;
using Plannit.Services;

namespace Plannit.Tests;

public class SnapshotImportTests
{
    [Fact]
    public void OfxParseLedgerBalance_ParsesBalanceAndAsOfDate()
    {
        var content = """
            <OFX>
            <BANKMSGSRSV1>
            <STMTTRNRS>
            <STMTRS>
            <LEDGERBAL>
            <BALAMT>-110.46
            <DTASOF>20260712120000[0:GMT]
            </LEDGERBAL>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            </OFX>
            """;

        var (balance, asOfDate) = OfxImportService.ParseLedgerBalance(content);

        Assert.Equal(-110.46m, balance);
        Assert.Equal(new DateOnly(2026, 7, 12), asOfDate);
    }

    [Fact]
    public void OfxParseLedgerBalance_HandlesNoTimezoneBracket()
    {
        var content = """
            <LEDGERBAL>
            <BALAMT>-205.48
            <DTASOF>20260712064819
            </LEDGERBAL>
            """;

        var (balance, asOfDate) = OfxImportService.ParseLedgerBalance(content);

        Assert.Equal(-205.48m, balance);
        Assert.Equal(new DateOnly(2026, 7, 12), asOfDate);
    }

    [Fact]
    public void OfxParseLedgerBalance_MissingBlock_ReturnsNulls()
    {
        var (balance, asOfDate) = OfxImportService.ParseLedgerBalance("<OFX>no ledger here</OFX>");

        Assert.Null(balance);
        Assert.Null(asOfDate);
    }

    [Fact]
    public void PositionsCsv_LooksLikePositionsExport_DetectsSymbolAndCurrentValueHeaders()
    {
        var headers = new List<string>
        {
            "Account number", "Account name", "Symbol", "Description", "Quantity",
            "Last price", "Current value", "Cost basis total"
        };

        Assert.True(PositionsCsvImportService.LooksLikePositionsExport(headers));
    }

    [Fact]
    public void PositionsCsv_LooksLikePositionsExport_FalseForTransactionHeaders()
    {
        var headers = new List<string> { "Trans. Date", "Post Date", "Description", "Amount", "Category" };

        Assert.False(PositionsCsvImportService.LooksLikePositionsExport(headers));
    }

    [Fact]
    public void PositionsCsv_Parse_SumsCurrentValueAndParsesDownloadDate()
    {
        var csv = "Account number,Account name,Symbol,Description,Quantity,Last price,Current value\n" +
                  "249345586,ROTH IRA,SPAXX**,HELD IN MONEY MARKET,,,\"$4.72\"\n" +
                  "249345586,ROTH IRA,FXAIX,FIDELITY 500 INDEX FUND,81.529,$263.26,\"$21463.32\"\n" +
                  "\n" +
                  "\"Some disclaimer paragraph with, commas, in it.\"\n" +
                  "\n" +
                  "\"Date downloaded Jul-12-2026 2:35 a.m ET\"\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = new PositionsCsvImportService();
        var preview = service.Parse(stream);

        Assert.True(preview.Success);
        Assert.Equal(2, preview.Positions.Count);
        Assert.Equal(21468.04m, preview.Total);
        Assert.True(preview.DateFromFile);
        Assert.Equal(new DateOnly(2026, 7, 12), preview.AsOfDate);
    }

    [Fact]
    public void PositionsCsv_Parse_CapturesQuantityPriceAndCostBasis()
    {
        var csv = "Account number,Account name,Symbol,Description,Quantity,Last price,Last price change,Current value,Today's gain/loss dollar,Today's gain/loss percent,Total gain/loss dollar,Total gain/loss percent,Percent of account,Cost basis total,Average cost basis,Type\n" +
                  "249345586,ROTH IRA,SPAXX**,HELD IN MONEY MARKET,,,,\"$4.72\",,,,,0.01%,,,Cash,\n" +
                  "249345586,ROTH IRA,FXAIX,FIDELITY 500 INDEX FUND,81.529,$263.26,+$0.43,\"$21463.32\",--,--,+$6460.69,+43.06%,55.54%,\"$15002.63\",$184.02,Cash,\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var preview = new PositionsCsvImportService().Parse(stream);

        Assert.True(preview.Success);
        var fxaix = preview.Positions.Single(p => p.Symbol == "FXAIX");
        Assert.Equal(81.529m, fxaix.Quantity);
        Assert.Equal(263.26m, fxaix.Price);
        Assert.Equal(21463.32m, fxaix.Value);
        Assert.Equal(15002.63m, fxaix.CostBasis);

        // Cash line has no quantity/price/cost basis.
        var spaxx = preview.Positions.Single(p => p.Symbol == "SPAXX**");
        Assert.Null(spaxx.Quantity);
        Assert.Null(spaxx.Price);
        Assert.Null(spaxx.CostBasis);
        Assert.Equal(4.72m, spaxx.Value);
    }

    [Fact]
    public void PositionsCsv_Parse_FallsBackToTodayWhenNoDownloadDateFound()
    {
        var csv = "Symbol,Description,Current value\nFXAIX,FIDELITY 500 INDEX FUND,\"$100.00\"\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = new PositionsCsvImportService();
        var preview = service.Parse(stream);

        Assert.True(preview.Success);
        Assert.False(preview.DateFromFile);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), preview.AsOfDate);
    }

    [Fact]
    public void PdfStatement_ParseFromText_ExtractsEndingBalanceAndBusDate()
    {
        var text = "Fees $0.00 -$5.09Gains/Loss $3,854.09 $2,983.51Endingbalance $35,857.59 $35,857.59Personalrateofreturn 12.47% 9.09%" +
                   "PLANID|NOPLAN|BUSDATE|20260630|SEQUENCE|0017541|END|";

        var preview = PdfStatementService.ParseFromText(text);

        Assert.Equal(35857.59m, preview.Balance);
        Assert.Equal(new DateOnly(2026, 6, 30), preview.AsOfDate);
    }

    [Fact]
    public void PdfStatement_ParseFromText_FallsBackToYourBalanceOnPhraseForBalanceAndDate()
    {
        var text = "QuarterlyretirementsavingsportfoliostatementForApril1,2026toJune30,2026" +
                   "YourbalanceonJune30,2026:$35,857.59Personalrateofreturnthisquarter:12.47%";

        var preview = PdfStatementService.ParseFromText(text);

        Assert.Equal(35857.59m, preview.Balance);
        Assert.Equal(new DateOnly(2026, 6, 30), preview.AsOfDate);
    }

    [Fact]
    public void PdfStatement_ParseFromText_NoMatchLeavesBalanceAndDateNull()
    {
        var preview = PdfStatementService.ParseFromText("This statement has no recognizable balance pattern at all.");

        Assert.Null(preview.Balance);
        Assert.Null(preview.AsOfDate);
        Assert.NotEmpty(preview.ExtractedText);
    }

    [Fact]
    public void PdfStatement_Parse_NeverThrowsOnUnparseableBytes()
    {
        // A real-world PdfPig parsing crash on a malformed page must degrade to an
        // editable confirm screen (null balance/date), never a 500.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf"));
        var service = new PdfStatementService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfStatementService>.Instance);

        var preview = service.Parse(stream);

        Assert.Null(preview.Balance);
        Assert.Null(preview.AsOfDate);
        Assert.NotEmpty(preview.ExtractedText);
    }
}
