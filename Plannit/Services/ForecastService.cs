using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

/// <summary>Projects a daily checking+savings balance forward using active bills and a
/// trailing 3-month average of non-bill ("discretionary") spend.</summary>
public class ForecastService
{
    private static readonly AccountType[] LiquidTypes = [AccountType.Checking, AccountType.Savings];
    private const string TransfersCategoryName = "Transfers";
    private const int DiscretionaryLookbackMonths = 3;

    private readonly ApplicationDbContext _db;
    private readonly BillService _billService;

    public ForecastService(ApplicationDbContext db, BillService billService)
    {
        _db = db;
        _billService = billService;
    }

    public async Task<ForecastResult> GetForecastAsync(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var latestBalances = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.IsActive && LiquidTypes.Contains(a.Type))
            .Select(a => a.Snapshots.OrderByDescending(s => s.Date).Select(s => (decimal?)s.Balance).FirstOrDefault())
            .ToListAsync();
        var startingBalance = latestBalances.Where(b => b.HasValue).Sum(b => b!.Value);

        var rangeEnd = today.AddDays(days);
        var occurrences = await _billService.GetOccurrencesAsync(today.AddDays(1), rangeEnd);
        var billOccurrences = occurrences
            .Select(o => (o.Date, SignedAmount: o.Bill.IsIncome ? o.Bill.ExpectedAmount : -o.Bill.ExpectedAmount))
            .ToList();

        var avgDailyDiscretionary = await GetAverageDailyDiscretionarySpendAsync(today);

        return ComputeForecast(startingBalance, today, days, billOccurrences, avgDailyDiscretionary);
    }

    /// <summary>Trailing-3-month average daily cash out, excluding Transfers-categorized
    /// transactions (moving money isn't spending) and transactions whose normalized merchant
    /// matches an active bill (already modeled explicitly as a discrete occurrence, so folding
    /// them into the daily average would double-count them).</summary>
    private async Task<decimal> GetAverageDailyDiscretionarySpendAsync(DateOnly today)
    {
        var start = today.AddMonths(-DiscretionaryLookbackMonths);
        var totalDays = today.DayNumber - start.DayNumber;
        if (totalDays <= 0) return 0m;

        var billMerchantKeys = await _db.Bills
            .AsNoTracking()
            .Where(b => b.IsActive && !b.IsIncome)
            .Select(b => b.MerchantKey)
            .ToListAsync();
        var billKeySet = billMerchantKeys.ToHashSet();

        var expenseTxns = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= start && t.Date <= today && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .Select(t => new { t.Amount, t.Description })
            .ToListAsync();

        var discretionaryTotal = expenseTxns
            .Where(t => !billKeySet.Contains(RecurringDetectionService.NormalizeMerchant(t.Description)))
            .Sum(t => t.Amount);

        return discretionaryTotal / totalDays;
    }

    /// <summary>Pure projection math, kept static and DB-free for unit testing: starting
    /// balance plus a smooth daily discretionary drift plus discrete bill occurrences,
    /// accumulated day by day with the first balance-below-zero date flagged.</summary>
    public static ForecastResult ComputeForecast(
        decimal startingBalance,
        DateOnly today,
        int days,
        List<(DateOnly Date, decimal SignedAmount)> billOccurrences,
        decimal avgDailyDiscretionary)
    {
        var byDay = billOccurrences
            .GroupBy(o => o.Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.SignedAmount));

        var points = new List<ForecastPoint>();
        var running = startingBalance;
        DateOnly? zeroCrossing = null;

        for (var i = 1; i <= days; i++)
        {
            var date = today.AddDays(i);
            running += avgDailyDiscretionary;
            if (byDay.TryGetValue(date, out var billNet))
                running += billNet;

            points.Add(new ForecastPoint { Date = date, Balance = running });

            if (zeroCrossing is null && running < 0)
                zeroCrossing = date;
        }

        return new ForecastResult
        {
            StartingBalance = startingBalance,
            AvgDailyDiscretionary = avgDailyDiscretionary,
            Points = points,
            ZeroCrossingDate = zeroCrossing
        };
    }
}

public class ForecastResult
{
    public decimal StartingBalance { get; set; }
    public decimal AvgDailyDiscretionary { get; set; }
    public List<ForecastPoint> Points { get; set; } = new();
    public DateOnly? ZeroCrossingDate { get; set; }
}

public class ForecastPoint
{
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }
}
