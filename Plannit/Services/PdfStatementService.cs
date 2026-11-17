using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Plannit.Services;

public class PdfStatementService
{
    private readonly ILogger<PdfStatementService> _logger;

    public PdfStatementService(ILogger<PdfStatementService> logger)
    {
        _logger = logger;
    }

    /// <summary>A statement's balance and date are on its first pages; later pages are not read.</summary>
    internal const int MaxPages = 100;
    /// <summary>Extracted text kept for parsing. A PDF can decompress to far more text than its file size.</summary>
    internal const int MaxTextLength = 1_000_000;

    // Uploaded statements are untrusted input: these run on the non-backtracking engine so matching
    // time stays linear in the text length (see OfxImportService for the failure this prevents).
    private const RegexOptions Linear = RegexOptions.NonBacktracking;
    private const RegexOptions LinearIgnoreCase = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex Whitespace = new(@"\s+", Linear, MatchTimeout);
    private static readonly Regex EndingBalance = new(@"endingbalance\$([\d,]+\.\d{2})", LinearIgnoreCase, MatchTimeout);
    private static readonly Regex BalanceOnAmount = new(@"yourbalanceon[^:]*:\$([\d,]+\.\d{2})", LinearIgnoreCase, MatchTimeout);
    private static readonly Regex BusinessDate = new(@"\|BUSDATE\|(\d{8})\|", Linear, MatchTimeout);
    private static readonly Regex BalanceOnDate = new(@"yourbalanceon([A-Za-z]+\d{1,2},\d{4}):", LinearIgnoreCase, MatchTimeout);
    private static readonly Regex PeriodEndDate = new(@"to([A-Za-z]+\d{1,2},\d{4})", LinearIgnoreCase, MatchTimeout);
    private static readonly Regex CompactDate = new(@"^([A-Za-z]+)(\d{1,2}),(\d{4})$", Linear, MatchTimeout);

    public PdfStatementPreview Parse(Stream pdfStream)
    {
        var sb = new StringBuilder();
        try
        {
            using var document = PdfDocument.Open(pdfStream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var pages = Math.Min(document.NumberOfPages, MaxPages);
            for (var i = 1; i <= pages && sb.Length < MaxTextLength; i++)
            {
                // PdfPig can throw on individual malformed pages even in lenient mode; skip rather than fail the whole statement.
                try
                {
                    sb.AppendLine(document.GetPage(i).Text);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract text from PDF page {PageNumber}", i);
                }
            }
        }
        catch (Exception ex)
        {
            // Document couldn't be opened at all — fall through with whatever text (if any) was extracted.
            _logger.LogWarning(ex, "Failed to open PDF statement for text extraction");
        }

        if (sb.Length > MaxTextLength) sb.Length = MaxTextLength;

        PdfStatementPreview preview;
        try
        {
            preview = ParseFromText(sb.ToString());
        }
        catch (RegexMatchTimeoutException)
        {
            preview = new PdfStatementPreview { ExtractedText = "This PDF took too long to read. Enter the balance and date manually." };
        }
        if (sb.Length == 0)
            preview.ExtractedText = "Could not extract text from this PDF. Enter the balance and date manually.";
        return preview;
    }

    internal static PdfStatementPreview ParseFromText(string rawText)
    {
        var compact = Whitespace.Replace(rawText, "");

        decimal? balance = null;
        var balanceMatch = EndingBalance.Match(compact);
        if (!balanceMatch.Success)
            balanceMatch = BalanceOnAmount.Match(compact);

        if (balanceMatch.Success)
            balance = decimal.Parse(balanceMatch.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);

        DateOnly? asOfDate = null;

        var busDateMatch = BusinessDate.Match(rawText);
        if (busDateMatch.Success &&
            DateOnly.TryParseExact(busDateMatch.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var busDate))
        {
            asOfDate = busDate;
        }

        if (asOfDate is null)
        {
            var balanceOnMatch = BalanceOnDate.Match(compact);
            if (balanceOnMatch.Success && TryParseCompactDate(balanceOnMatch.Groups[1].Value, out var parsedDate))
                asOfDate = parsedDate;
        }

        if (asOfDate is null)
        {
            var periodMatch = PeriodEndDate.Match(compact);
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
        var m = CompactDate.Match(compactDate);
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
