using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class ReportsService
{
    private readonly ApplicationDbContext _db;
    private const string TransfersCategoryName = "Transfers";

    public ReportsService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<SpendByCategoryResult> GetSpendByCategoryAsync(DateOnly startDate, DateOnly endDate)
    {
        var grouped = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Date <= endDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .GroupBy(t => t.Category != null ? t.Category.Name : null)
            .Select(g => new CategorySpend
            {
                CategoryName = g.Key ?? "Uncategorized",
                Total = -g.Sum(t => t.Amount)
            })
            .OrderByDescending(c => c.Total)
            .ToListAsync();

        return new SpendByCategoryResult
        {
            Categories = grouped,
            TotalSpend = grouped.Sum(c => c.Total)
        };
    }

    public async Task<List<MonthlySpend>> GetMonthlySpendHistoryAsync(int months = 12)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var startDate = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        var grouped = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .GroupBy(t => new { t.Date.Year, t.Date.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = -g.Sum(t => t.Amount) })
            .ToDictionaryAsync(g => (g.Year, g.Month), g => g.Total);

        var result = new List<MonthlySpend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            grouped.TryGetValue((monthStart.Year, monthStart.Month), out var total);

            result.Add(new MonthlySpend
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Label = monthStart.ToString("MMM yyyy"),
                Total = total
            });
        }

        return result;
    }

    public async Task<IncomeExpenseSummary> GetIncomeExpenseSummaryAsync(DateOnly startDate, DateOnly endDate)
    {
        var query = _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Date <= endDate)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName);

        var income = await query.Where(t => t.Amount > 0).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var expenses = await query.Where(t => t.Amount < 0).SumAsync(t => (decimal?)t.Amount) ?? 0m;

        return new IncomeExpenseSummary
        {
            Income = income,
            Expenses = -expenses,
        };
    }

    private static readonly string[] CardPaymentPhrasings =
    [
        "Payment to Chase card", "AUTOMATIC PAYMENT", "PAYMENT THANK YOU",
        "DIRECTPAY", "Online payment", "ACH DEPOSIT INTERNET TRANSFER", "Card Payment"
    ];

    /// <summary>
    /// A transfer only nets to zero if both legs of the movement are tracked accounts — an
    /// external transfer (Venmo cashout, transfer to an untracked account) never pairs and
    /// would otherwise cry wolf every period. Pair opposite-amount transactions across
    /// accounts within a few days of each other; only unpaired transactions that look like
    /// an inter-account card payment are worth a warning, everything else unpaired is
    /// reported informationally.
    /// </summary>
    // Bucket size matches the 5-day pairing window: two transactions within 5 days of each
    // other land in the same bucket or an adjacent one, so scanning bucket-1..bucket+1 finds
    // every candidate the old full O(n^2) scan did, without comparing transactions that are
    // amount- or date-incompatible up front.
    private const int TransferDateBucketDays = 5;

    public async Task<TransfersSanityResult> GetTransfersSanityAsync(DateOnly startDate, DateOnly endDate)
    {
        var transfers = await _db.Transactions
            .AsNoTracking()
            .Include(t => t.Category)
            .Include(t => t.Account)
            .Where(t => t.Date >= startDate && t.Date <= endDate)
            .Where(t => t.Category != null && t.Category.Name == TransfersCategoryName)
            .OrderBy(t => t.Date)
            .ToListAsync();

        var buckets = new Dictionary<(decimal AbsAmount, int DateBucket), List<Transaction>>();
        foreach (var t in transfers)
        {
            var key = (Math.Abs(t.Amount), t.Date.DayNumber / TransferDateBucketDays);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<Transaction>();
            list.Add(t);
        }

        var candidates = new List<(Transaction A, Transaction B, int DateDiff)>();
        var seenPairs = new HashSet<(int, int)>();
        foreach (var a in transfers)
        {
            var absAmount = Math.Abs(a.Amount);
            var bucketIndex = a.Date.DayNumber / TransferDateBucketDays;

            for (var b = bucketIndex - 1; b <= bucketIndex + 1; b++)
            {
                if (!buckets.TryGetValue((absAmount, b), out var bucketTransfers)) continue;

                foreach (var candidateB in bucketTransfers)
                {
                    if (candidateB.Id == a.Id) continue;
                    var pairKey = a.Id < candidateB.Id ? (a.Id, candidateB.Id) : (candidateB.Id, a.Id);
                    if (!seenPairs.Add(pairKey)) continue;

                    if (a.AccountId == candidateB.AccountId) continue;
                    if (Math.Abs(a.Amount + candidateB.Amount) > 0.01m) continue;

                    var dateDiff = Math.Abs(a.Date.DayNumber - candidateB.Date.DayNumber);
                    if (dateDiff > 5) continue;

                    candidates.Add((a, candidateB, dateDiff));
                }
            }
        }

        var paired = new HashSet<int>();
        foreach (var candidate in candidates.OrderBy(c => c.DateDiff))
        {
            if (paired.Contains(candidate.A.Id) || paired.Contains(candidate.B.Id)) continue;
            paired.Add(candidate.A.Id);
            paired.Add(candidate.B.Id);
        }

        var unpaired = transfers.Where(t => !paired.Contains(t.Id)).ToList();
        var suspects = unpaired.Where(MatchesCardPaymentPattern).ToList();
        var external = unpaired.Where(t => !suspects.Contains(t)).ToList();

        return new TransfersSanityResult
        {
            TransactionCount = transfers.Count,
            PairedTransactionCount = paired.Count,
            UnpairedSuspects = suspects
                .OrderByDescending(t => Math.Abs(t.Amount))
                .Select(t => new TransferSuspect
                {
                    TransactionId = t.Id,
                    Date = t.Date,
                    Description = t.Description,
                    Amount = t.Amount,
                    AccountName = t.Account.Name
                })
                .ToList(),
            ExternalTransfersIn = external.Where(t => t.Amount > 0).Sum(t => t.Amount),
            ExternalTransfersOut = Math.Abs(external.Where(t => t.Amount < 0).Sum(t => t.Amount)),
            ExternalTransfersCount = external.Count
        };
    }

    private static bool MatchesCardPaymentPattern(Transaction t)
    {
        return CardPaymentPhrasings.Any(p => t.Description.Contains(p, StringComparison.OrdinalIgnoreCase))
            || (t.OriginalDescription is not null && CardPaymentPhrasings.Any(p => t.OriginalDescription.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<List<MerchantSpend>> GetTopMerchantsAsync(DateOnly startDate, DateOnly endDate, int count = 10)
    {
        return await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Date >= startDate && t.Date <= endDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .GroupBy(t => t.Description)
            .Select(g => new MerchantSpend
            {
                MerchantName = g.Key,
                Total = -g.Sum(t => t.Amount),
                TransactionCount = g.Count()
            })
            .OrderByDescending(m => m.Total)
            .Take(count)
            .ToListAsync();
    }
}

public class SpendByCategoryResult
{
    public List<CategorySpend> Categories { get; set; } = new();
    public decimal TotalSpend { get; set; }
}

public class CategorySpend
{
    public string CategoryName { get; set; } = null!;
    public decimal Total { get; set; }
}

public class MonthlySpend
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string Label { get; set; } = null!;
    public decimal Total { get; set; }
}

public class IncomeExpenseSummary
{
    public decimal Income { get; set; }
    public decimal Expenses { get; set; }
    public decimal Net => Income - Expenses;
}

public class MerchantSpend
{
    public string MerchantName { get; set; } = null!;
    public decimal Total { get; set; }
    public int TransactionCount { get; set; }
}

public class TransfersSanityResult
{
    public int TransactionCount { get; set; }
    public int PairedTransactionCount { get; set; }
    public List<TransferSuspect> UnpairedSuspects { get; set; } = new();
    public decimal ExternalTransfersIn { get; set; }
    public decimal ExternalTransfersOut { get; set; }
    public int ExternalTransfersCount { get; set; }
    public bool HasUnpairedSuspects => UnpairedSuspects.Count > 0;
    public decimal UnpairedSuspectTotal => UnpairedSuspects.Sum(s => Math.Abs(s.Amount));
}

public class TransferSuspect
{
    public int TransactionId { get; set; }
    public DateOnly Date { get; set; }
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public string AccountName { get; set; } = null!;
}
