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
        var transactions = await _db.Transactions
            .Include(t => t.Category)
            .Where(t => t.Date >= startDate && t.Date <= endDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .ToListAsync();

        var grouped = transactions
            .GroupBy(t => t.Category?.Name ?? "Uncategorized")
            .Select(g => new CategorySpend
            {
                CategoryName = g.Key,
                Total = Math.Abs(g.Sum(t => t.Amount))
            })
            .OrderByDescending(c => c.Total)
            .ToList();

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

        var transactions = await _db.Transactions
            .Include(t => t.Category)
            .Where(t => t.Date >= startDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .ToListAsync();

        var result = new List<MonthlySpend>();
        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            var monthTxns = transactions
                .Where(t => t.Date >= monthStart && t.Date <= monthEnd);

            result.Add(new MonthlySpend
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Label = monthStart.ToString("MMM yyyy"),
                Total = Math.Abs(monthTxns.Sum(t => t.Amount))
            });
        }

        return result;
    }

    public async Task<IncomeExpenseSummary> GetIncomeExpenseSummaryAsync(DateOnly startDate, DateOnly endDate)
    {
        var transactions = await _db.Transactions
            .Include(t => t.Category)
            .Where(t => t.Date >= startDate && t.Date <= endDate)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .ToListAsync();

        return new IncomeExpenseSummary
        {
            Income = transactions.Where(t => t.Amount > 0).Sum(t => t.Amount),
            Expenses = Math.Abs(transactions.Where(t => t.Amount < 0).Sum(t => t.Amount)),
        };
    }

    public async Task<List<MerchantSpend>> GetTopMerchantsAsync(DateOnly startDate, DateOnly endDate, int count = 10)
    {
        var transactions = await _db.Transactions
            .Include(t => t.Category)
            .Where(t => t.Date >= startDate && t.Date <= endDate && t.Amount < 0)
            .Where(t => t.Category == null || t.Category.Name != TransfersCategoryName)
            .ToListAsync();

        var merchants = transactions
            .GroupBy(t => t.Description)
            .Select(g => new MerchantSpend
            {
                MerchantName = g.Key,
                Total = Math.Abs(g.Sum(t => t.Amount)),
                TransactionCount = g.Count()
            })
            .OrderByDescending(m => m.Total)
            .Take(count)
            .ToList();

        return merchants;
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
