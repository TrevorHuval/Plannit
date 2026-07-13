using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class RecurringDetectionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public RecurringDetectionService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<List<RecurringGroup>> DetectRecurringAsync()
    {
        var key = $"recurring:{_db.CurrentUserId}:{_db.CacheVersion}";
        if (_cache.TryGetValue(key, out List<RecurringGroup>? cached) && cached is not null)
            return cached;

        var transactions = await _db.Transactions
            .AsNoTracking()
            .OrderBy(t => t.Date)
            .ToListAsync();

        var result = DetectFromTransactions(transactions);
        _cache.Set(key, result, CacheTtl);
        return result;
    }

    public static List<RecurringGroup> DetectFromTransactions(List<Transaction> transactions)
    {
        var groups = transactions
            .Where(t => t.Amount != 0)
            .GroupBy(t => NormalizeMerchant(t.Description))
            .Where(g => g.Key.Length > 0 && g.Count() >= 2);

        var results = new List<RecurringGroup>();

        foreach (var group in groups)
        {
            var isIncome = group.All(t => t.Amount > 0);
            var isExpense = group.All(t => t.Amount < 0);
            if (!isIncome && !isExpense) continue;

            var sorted = group.OrderBy(t => t.Date).ToList();

            var amountAnalysis = AnalyzeAmounts(sorted.Select(t => Math.Abs(t.Amount)).ToList());
            if (amountAnalysis is null) continue;

            var cadenceAnalysis = DetectCadence(sorted.Select(t => t.Date).ToList());
            if (cadenceAnalysis is null) continue;

            var lastDate = sorted.Last().Date;
            var nextExpected = EstimateNextDate(lastDate, cadenceAnalysis.Value.Cadence);
            var periodDays = CadencePeriodDays(cadenceAnalysis.Value.Cadence);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var recentlyStopped = (today.DayNumber - nextExpected.DayNumber) > periodDays * 1.5;

            var displayDescription = sorted
                .Select(t => t.Description)
                .OrderBy(d => d.Length)
                .ThenBy(d => d, StringComparer.Ordinal)
                .First();

            var confidence = Math.Round(cadenceAnalysis.Value.Regularity * amountAnalysis.Value.Consistency, 2);

            results.Add(new RecurringGroup
            {
                Description = displayDescription,
                Cadence = cadenceAnalysis.Value.Cadence,
                Nature = amountAnalysis.Value.Nature,
                IsIncome = isIncome,
                AverageAmount = Math.Round(sorted.Select(t => Math.Abs(t.Amount)).Average(), 2),
                LastSeen = lastDate,
                NextExpected = nextExpected,
                OccurrenceCount = sorted.Count,
                Confidence = confidence,
                RecentlyStopped = recentlyStopped,
                Transactions = sorted
            });
        }

        return results.OrderByDescending(r => r.AnnualizedCost).ToList();
    }

    public async Task<List<RecurringGroup>> GetUpcomingAsync(int daysAhead = 7)
    {
        var all = await DetectRecurringAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cutoff = today.AddDays(daysAhead);

        return all
            .Where(r => !r.IsIncome && r.NextExpected >= today && r.NextExpected <= cutoff)
            .OrderBy(r => r.NextExpected)
            .ToList();
    }

    // ===== Merchant normalization =====

    private static readonly Regex ApplePayTail = new(@"\bAPPLE PAY ENDING IN\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AsteriskRefSuffix = new(@"\*[A-Za-z0-9]{3,}$", RegexOptions.Compiled);
    private static readonly Regex HashRefSuffix = new(@"#\d+", RegexOptions.Compiled);
    private static readonly Regex LongDigitRun = new(@"\b\d{4,}\b", RegexOptions.Compiled);
    private static readonly Regex ExtraWhitespace = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly HashSet<string> UsStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA",
        "KS", "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
        "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT",
        "VA", "WA", "WV", "WI", "WY", "DC"
    };

    /// <summary>
    /// Strips reference noise (card-network suffixes, store/confirmation numbers, Apple Pay
    /// tails, trailing city/state) from a raw transaction description so that repeated charges
    /// from the same merchant group together even when each posting carries a unique suffix.
    /// </summary>
    public static string NormalizeMerchant(string description)
    {
        var text = (description ?? string.Empty).Trim();
        if (text.Length == 0) return text;

        text = ApplePayTail.Replace(text, "").Trim();
        text = AsteriskRefSuffix.Replace(text, "").Trim();
        text = HashRefSuffix.Replace(text, "").Trim();
        text = LongDigitRun.Replace(text, "").Trim();

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2
            && tokens[^1].Length == 2
            && UsStateCodes.Contains(tokens[^1])
            && tokens[^2].Any(char.IsLetter))
        {
            tokens = tokens[..^2];
            text = string.Join(' ', tokens);
        }

        text = ExtraWhitespace.Replace(text, " ").Trim();
        text = text.Trim('*', '#', '-', '.', ' ');
        return text.ToUpperInvariant();
    }

    // ===== Amount tolerance =====

    private static (AmountNature Nature, double Consistency)? AnalyzeAmounts(List<decimal> amounts)
    {
        var median = Median(amounts);
        if (median <= 0) return null;

        var maxDeviation = amounts.Max(a => Math.Abs((double)((a - median) / median)));

        if (maxDeviation <= 0.10)
            return (AmountNature.Fixed, 1.0 - maxDeviation);
        if (maxDeviation <= 0.35)
            return (AmountNature.Variable, Math.Max(0.0, 1.0 - maxDeviation / 0.35));

        return null;
    }

    private static decimal Median(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;
    }

    // ===== Cadence detection =====

    private readonly record struct CadenceBand(RecurringCadence Cadence, int MinDays, int MaxDays);

    private static readonly CadenceBand[] CadenceBands =
    [
        new(RecurringCadence.Weekly, 5, 9),
        new(RecurringCadence.Biweekly, 12, 16),
        new(RecurringCadence.Monthly, 26, 35),
        new(RecurringCadence.Quarterly, 80, 100),
        new(RecurringCadence.Yearly, 350, 380)
    ];

    private static (RecurringCadence Cadence, double Regularity)? DetectCadence(List<DateOnly> dates)
    {
        if (dates.Count < 2) return null;

        var intervals = new List<int>();
        for (var i = 1; i < dates.Count; i++)
            intervals.Add(dates[i].DayNumber - dates[i - 1].DayNumber);

        if (dates.Count == 2)
        {
            var interval = intervals[0];
            if (interval >= 350 && interval <= 380 && dates[0].Month == dates[1].Month)
                return (RecurringCadence.Yearly, 1.0);
            return null;
        }

        var median = MedianInt(intervals);

        foreach (var band in CadenceBands)
        {
            if (median < band.MinDays || median > band.MaxDays) continue;

            var goodCount = intervals.Count(interval =>
                (interval >= band.MinDays && interval <= band.MaxDays) || IsToleratedSkip(interval, median));

            var regularity = (double)goodCount / intervals.Count;
            if (regularity >= 0.70)
                return (band.Cadence, regularity);
        }

        return null;
    }

    private static bool IsToleratedSkip(int interval, double median)
    {
        if (median <= 0) return false;
        var ratio = interval / median;
        return ratio >= 1.75 && ratio <= 2.5;
    }

    private static double MedianInt(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static DateOnly EstimateNextDate(DateOnly lastDate, RecurringCadence cadence) => cadence switch
    {
        RecurringCadence.Weekly => lastDate.AddDays(7),
        RecurringCadence.Biweekly => lastDate.AddDays(14),
        RecurringCadence.Monthly => lastDate.AddMonths(1),
        RecurringCadence.Quarterly => lastDate.AddMonths(3),
        RecurringCadence.Yearly => lastDate.AddYears(1),
        _ => lastDate.AddMonths(1)
    };

    private static int CadencePeriodDays(RecurringCadence cadence) => cadence switch
    {
        RecurringCadence.Weekly => 7,
        RecurringCadence.Biweekly => 14,
        RecurringCadence.Monthly => 30,
        RecurringCadence.Quarterly => 91,
        RecurringCadence.Yearly => 365,
        _ => 30
    };
}

public enum RecurringCadence
{
    Weekly,
    Biweekly,
    Monthly,
    Quarterly,
    Yearly
}

public enum AmountNature
{
    Fixed,
    Variable
}

public class RecurringGroup
{
    public string Description { get; set; } = null!;
    public RecurringCadence Cadence { get; set; }
    public AmountNature Nature { get; set; }
    public bool IsIncome { get; set; }
    public decimal AverageAmount { get; set; }
    public DateOnly LastSeen { get; set; }
    public DateOnly NextExpected { get; set; }
    public int OccurrenceCount { get; set; }
    public double Confidence { get; set; }
    public bool RecentlyStopped { get; set; }
    public List<Transaction> Transactions { get; set; } = new();

    public string CadenceLabel => Cadence switch
    {
        RecurringCadence.Weekly => "Weekly",
        RecurringCadence.Biweekly => "Biweekly",
        RecurringCadence.Monthly => "Monthly",
        RecurringCadence.Quarterly => "Quarterly",
        RecurringCadence.Yearly => "Yearly",
        _ => "Unknown"
    };

    public string NatureLabel => Nature == AmountNature.Fixed ? "Fixed" : "Variable";

    public decimal AnnualizedCost => Cadence switch
    {
        RecurringCadence.Weekly => AverageAmount * 52,
        RecurringCadence.Biweekly => AverageAmount * 26,
        RecurringCadence.Monthly => AverageAmount * 12,
        RecurringCadence.Quarterly => AverageAmount * 4,
        RecurringCadence.Yearly => AverageAmount,
        _ => AverageAmount * 12
    };
}
