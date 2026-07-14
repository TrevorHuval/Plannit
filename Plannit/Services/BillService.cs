using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class BillService
{
    // A matched transaction's amount must fall within this fraction of ExpectedAmount to
    // count as payment of a Fixed or Variable bill.
    private const decimal AmountTolerance = 0.15m;
    private const int MaxCatchUpIterations = 12;

    private readonly ApplicationDbContext _db;

    public BillService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Bill>> GetAllAsync()
    {
        return await _db.Bills
            .AsNoTracking()
            .OrderBy(b => b.NextDue)
            .ToListAsync();
    }

    public async Task<Bill?> GetByIdAsync(int id)
    {
        return await _db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<List<Bill>> GetUpcomingAsync(int daysAhead = 7)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var cutoff = today.AddDays(daysAhead);

        return await _db.Bills
            .AsNoTracking()
            .Where(b => b.IsActive && !b.IsIncome && b.NextDue >= today && b.NextDue <= cutoff)
            .OrderBy(b => b.NextDue)
            .ToListAsync();
    }

    /// <summary>Expands every active bill's cadence into individual occurrence dates falling
    /// within [rangeStart, rangeEnd] — the shared projection used by both the calendar grid
    /// and the cash-flow forecast.</summary>
    public async Task<List<BillOccurrence>> GetOccurrencesAsync(DateOnly rangeStart, DateOnly rangeEnd)
    {
        var bills = await _db.Bills.AsNoTracking().Where(b => b.IsActive).ToListAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var occurrences = new List<BillOccurrence>();
        foreach (var bill in bills)
        {
            foreach (var date in ProjectOccurrences(bill.NextDue, bill.Cadence, rangeStart, rangeEnd))
            {
                occurrences.Add(new BillOccurrence
                {
                    Bill = bill,
                    Date = date,
                    IsOverdue = date < today
                });
            }
        }

        return occurrences.OrderBy(o => o.Date).ToList();
    }

    /// <summary>Steps forward from <paramref name="anchor"/> by <paramref name="cadence"/>,
    /// fast-forwarding past any occurrences before <paramref name="rangeStart"/> (an overdue
    /// bill's NextDue can be well before the visible window) and collecting every occurrence
    /// through <paramref name="rangeEnd"/>. Bounded so a corrupt/degenerate cadence can't loop
    /// forever.</summary>
    public static List<DateOnly> ProjectOccurrences(DateOnly anchor, RecurringCadence cadence, DateOnly rangeStart, DateOnly rangeEnd)
    {
        var result = new List<DateOnly>();
        if (anchor > rangeEnd) return result;

        var date = anchor;
        var guard = 0;
        while (date < rangeStart && guard < 1000)
        {
            date = RecurringDetectionService.EstimateNextDate(date, cadence);
            guard++;
        }

        while (date <= rangeEnd && guard < 2000)
        {
            result.Add(date);
            date = RecurringDetectionService.EstimateNextDate(date, cadence);
            guard++;
        }

        return result;
    }

    public async Task<Bill> CreateAsync(string userId, string name, RecurringCadence cadence, decimal expectedAmount, DateOnly nextDue, bool isIncome)
    {
        var bill = new Bill
        {
            UserId = userId,
            MerchantKey = RecurringDetectionService.NormalizeMerchant(name),
            Name = name,
            Cadence = cadence,
            ExpectedAmount = Math.Abs(expectedAmount),
            NextDue = nextDue,
            IsIncome = isIncome,
            Source = BillSource.Manual,
            IsActive = true
        };
        _db.Bills.Add(bill);
        await _db.SaveChangesAsync();
        return bill;
    }

    /// <summary>Promotes a detected <see cref="RecurringGroup"/> into a persistent Bill.
    /// Idempotent on (MerchantKey, IsIncome) so re-clicking "promote" on the same detected
    /// group reactivates the existing bill instead of creating a duplicate.</summary>
    public async Task<Bill> PromoteAsync(string userId, string description, RecurringCadence cadence, decimal averageAmount, DateOnly nextExpected, bool isIncome)
    {
        var merchantKey = RecurringDetectionService.NormalizeMerchant(description);
        var existing = await _db.Bills.FirstOrDefaultAsync(b => b.MerchantKey == merchantKey && b.IsIncome == isIncome);
        if (existing is not null)
        {
            existing.IsActive = true;
            await _db.SaveChangesAsync();
            return existing;
        }

        var bill = new Bill
        {
            UserId = userId,
            MerchantKey = merchantKey,
            Name = description,
            Cadence = cadence,
            ExpectedAmount = Math.Abs(averageAmount),
            NextDue = nextExpected,
            IsIncome = isIncome,
            Source = BillSource.Detected,
            IsActive = true
        };
        _db.Bills.Add(bill);
        await _db.SaveChangesAsync();
        return bill;
    }

    public async Task<bool> UpdateAsync(int id, string name, RecurringCadence cadence, decimal expectedAmount, DateOnly nextDue, bool isIncome)
    {
        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == id);
        if (bill is null) return false;

        bill.Name = name;
        bill.MerchantKey = RecurringDetectionService.NormalizeMerchant(name);
        bill.Cadence = cadence;
        bill.ExpectedAmount = Math.Abs(expectedAmount);
        bill.NextDue = nextDue;
        bill.IsIncome = isIncome;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DismissAsync(int id)
    {
        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == id);
        if (bill is null) return false;

        bill.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == id);
        if (bill is null) return false;

        _db.Bills.Remove(bill);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Matches active bills against imported transactions (merchant key + amount
    /// tolerance + a cadence-sized date window around NextDue) and advances NextDue past
    /// each match found, catching up multiple missed occurrences in one pass. Safe to call
    /// on every page load — a bill's window only looks around its *current* NextDue, so a
    /// transaction already reconciled can't be matched again once NextDue has moved past it.
    /// Returns the number of occurrences reconciled.</summary>
    public async Task<int> ReconcileAsync()
    {
        var bills = await _db.Bills.Where(b => b.IsActive).ToListAsync();
        if (bills.Count == 0) return 0;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var earliestNeeded = bills.Min(b => b.NextDue).AddDays(-60);
        var latestNeeded = today.AddDays(400);

        var candidates = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= earliestNeeded && t.Date <= latestNeeded)
            .Select(t => new { t.Id, t.Date, t.Amount, t.Description })
            .ToListAsync();

        var matchedCount = 0;
        foreach (var bill in bills)
        {
            for (var i = 0; i < MaxCatchUpIterations; i++)
            {
                var windowDays = CadenceWindowDays(bill.Cadence);
                var windowStart = bill.NextDue.AddDays(-windowDays);
                var windowEnd = bill.NextDue.AddDays(windowDays);

                var match = candidates
                    .Where(t => t.Date >= windowStart && t.Date <= windowEnd)
                    .Where(t => bill.IsIncome ? t.Amount > 0 : t.Amount < 0)
                    .Where(t => Math.Abs(Math.Abs(t.Amount) - bill.ExpectedAmount) <= bill.ExpectedAmount * AmountTolerance)
                    .Where(t => RecurringDetectionService.NormalizeMerchant(t.Description) == bill.MerchantKey)
                    .OrderBy(t => Math.Abs(t.Date.DayNumber - bill.NextDue.DayNumber))
                    .FirstOrDefault();

                if (match is null) break;

                bill.LastPaidDate = match.Date;
                bill.LastPaidTransactionId = match.Id;
                bill.NextDue = RecurringDetectionService.EstimateNextDate(match.Date, bill.Cadence);
                matchedCount++;
            }
        }

        if (matchedCount > 0)
            await _db.SaveChangesAsync();

        return matchedCount;
    }

    private static int CadenceWindowDays(RecurringCadence cadence) => cadence switch
    {
        RecurringCadence.Weekly => 3,
        RecurringCadence.Biweekly => 5,
        RecurringCadence.Monthly => 7,
        RecurringCadence.Quarterly => 12,
        RecurringCadence.Yearly => 20,
        _ => 7
    };
}

public class BillOccurrence
{
    public Bill Bill { get; set; } = null!;
    public DateOnly Date { get; set; }
    public bool IsOverdue { get; set; }
}
