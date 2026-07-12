using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Plannit.Services;

public class PdfStatementService
{
    public PdfStatementPreview Parse(Stream pdfStream)
    {
        var sb = new StringBuilder();
        try
        {
            using var document = PdfDocument.Open(pdfStream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            for (var i = 1; i <= document.NumberOfPages; i++)
            {
                // PdfPig can throw on individual malformed pages even in lenient mode; skip rather than fail the whole statement.
                try
                {
                    sb.AppendLine(document.GetPage(i).Text);
                }
                catch
                {
                    // continue to next page
                }
            }
        }
        catch
        {
            // Document couldn't be opened at all — fall through with whatever text (if any) was extracted.
        }

        var preview = ParseFromText(sb.ToString());
        if (sb.Length == 0)
            preview.ExtractedText = "Could not extract text from this PDF. Enter the balance and date manually.";
        return preview;
    }

    internal static PdfStatementPreview ParseFromText(string rawText)
    {
        var compact = Regex.Replace(rawText, @"\s+", "");

        decimal? balance = null;
        var balanceMatch = Regex.Match(compact, @"endingbalance\$([\d,]+\.\d{2})", RegexOptions.IgnoreCase);
        if (!balanceMatch.Success)
            balanceMatch = Regex.Match(compact, @"yourbalanceon[^:]*:\$([\d,]+\.\d{2})", RegexOptions.IgnoreCase);

        if (balanceMatch.Success)
            balance = decimal.Parse(balanceMatch.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);

        DateOnly? asOfDate = null;

        var busDateMatch = Regex.Match(rawText, @"\|BUSDATE\|(\d{8})\|");
        if (busDateMatch.Success &&
            DateOnly.TryParseExact(busDateMatch.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var busDate))
        {
            asOfDate = busDate;
        }

        if (asOfDate is null)
        {
            var balanceOnMatch = Regex.Match(compact, @"yourbalanceon([A-Za-z]+\d{1,2},\d{4}):", RegexOptions.IgnoreCase);
            if (balanceOnMatch.Success && TryParseCompactDate(balanceOnMatch.Groups[1].Value, out var parsedDate))
                asOfDate = parsedDate;
        }

        if (asOfDate is null)
        {
            var periodMatch = Regex.Match(compact, @"to([A-Za-z]+\d{1,2},\d{4})", RegexOptions.IgnoreCase);
            if (periodMatch.Success && TryParseCompactDate(periodMatch.Groups[1].Value, out var parsedDate))
                asOfDate = parsedDate;
        }

        return new PdfStatementPreview
        {
            Balance = balance,
            AsOfDate = asOfDate,
            ExtractedText = rawText.Length > 4000 ? rawText[..4000] : rawText
        };
    }

    private static bool TryParseCompactDate(string compactDate, out DateOnly date)
    {
        date = default;
        var m = Regex.Match(compactDate, @"^([A-Za-z]+)(\d{1,2}),(\d{4})$");
        if (!m.Success) return false;

        var normalized = $"{m.Groups[1].Value} {m.Groups[2].Value}, {m.Groups[3].Value}";
        return DateOnly.TryParseExact(normalized, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParseExact(normalized, "MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}

public class PdfStatementPreview
{
    public decimal? Balance { get; set; }
    public DateOnly? AsOfDate { get; set; }
    public string ExtractedText { get; set; } = "";
}
