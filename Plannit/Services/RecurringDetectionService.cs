using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class RecurringDetectionService
{
    private readonly ApplicationDbContext _db;

    public RecurringDetectionService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<RecurringGroup>> DetectRecurringAsync()
    {
        var transactions = await _db.Transactions
            .Where(t => t.Amount < 0)
            .OrderBy(t => t.Date)
            .ToListAsync();

        return DetectFromTransactions(transactions);
    }

    public static List<RecurringGroup> DetectFromTransactions(List<Transaction> transactions)
    {
        var groups = transactions
            .Where(t => t.Amount < 0)
            .GroupBy(t => NormalizeDescription(t.Description))
            .Where(g => g.Count() >= 3)
            .ToList();

        var results = new List<RecurringGroup>();

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(t => t.Date).ToList();
            var amounts = sorted.Select(t => Math.Abs(t.Amount)).ToList();

            if (!HasSimilarAmounts(amounts)) continue;

            var cadence = DetectCadence(sorted.Select(t => t.Date).ToList());
            if (cadence is null) continue;

            var avgAmount = amounts.Average();
            var lastDate = sorted.Last().Date;
            var nextExpected = EstimateNextDate(lastDate, cadence.Value);

            results.Add(new RecurringGroup
            {
                Description = sorted.First().Description,
                Cadence = cadence.Value,
                AverageAmount = Math.Round((decimal)avgAmount, 2),
                LastSeen = lastDate,
                NextExpected = nextExpected,
                OccurrenceCount = sorted.Count,
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
            .Where(r => r.NextExpected >= today && r.NextExpected <= cutoff)
            .OrderBy(r => r.NextExpected)
            .ToList();
    }

    static string NormalizeDescription(string description)
    {
        var normalized = description.Trim();
        if (normalized.Length > 30)
            normalized = normalized[..30];
        return normalized.ToUpperInvariant();
    }

    static bool HasSimilarAmounts(List<decimal> amounts)
    {
        if (amounts.Count < 2) return true;
        var avg = amounts.Average();
        if (avg == 0) return false;
        return amounts.All(a => Math.Abs((double)(a - (decimal)avg) / (double)(decimal)avg) <= 0.20);
    }

    static RecurringCadence? DetectCadence(List<DateOnly> dates)
    {
        if (dates.Count < 3) return null;

        var intervals = new List<int>();
        for (int i = 1; i < dates.Count; i++)
            intervals.Add(dates[i].DayNumber - dates[i - 1].DayNumber);

        var avgInterval = intervals.Average();

        if (intervals.All(d => Math.Abs(d - 7) <= 2))
            return RecurringCadence.Weekly;
        if (intervals.All(d => Math.Abs(d - 30) <= 5))
            return RecurringCadence.Monthly;
        if (intervals.All(d => Math.Abs(d - 365) <= 15))
            return RecurringCadence.Yearly;

        if (avgInterval >= 5 && avgInterval <= 9 && intervals.All(d => Math.Abs(d - avgInterval) <= 2))
            return RecurringCadence.Weekly;
        if (avgInterval >= 25 && avgInterval <= 35 && intervals.All(d => Math.Abs(d - avgInterval) <= 5))
            return RecurringCadence.Monthly;
        if (avgInterval >= 350 && avgInterval <= 380 && intervals.All(d => Math.Abs(d - avgInterval) <= 15))
            return RecurringCadence.Yearly;

        return null;
    }

    static DateOnly EstimateNextDate(DateOnly lastDate, RecurringCadence cadence)
    {
        return cadence switch
        {
            RecurringCadence.Weekly => lastDate.AddDays(7),
            RecurringCadence.Monthly => lastDate.AddMonths(1),
            RecurringCadence.Yearly => lastDate.AddYears(1),
            _ => lastDate.AddMonths(1)
        };
    }
}

public enum RecurringCadence
{
    Weekly,
    Monthly,
    Yearly
}

public class RecurringGroup
{
    public string Description { get; set; } = null!;
    public RecurringCadence Cadence { get; set; }
    public decimal AverageAmount { get; set; }
    public DateOnly LastSeen { get; set; }
    public DateOnly NextExpected { get; set; }
    public int OccurrenceCount { get; set; }
    public List<Transaction> Transactions { get; set; } = new();

    public string CadenceLabel => Cadence switch
    {
        RecurringCadence.Weekly => "Weekly",
        RecurringCadence.Monthly => "Monthly",
        RecurringCadence.Yearly => "Yearly",
        _ => "Unknown"
    };

    public decimal AnnualizedCost => Cadence switch
    {
        RecurringCadence.Weekly => AverageAmount * 52,
        RecurringCadence.Monthly => AverageAmount * 12,
        RecurringCadence.Yearly => AverageAmount,
        _ => AverageAmount * 12
    };
}
