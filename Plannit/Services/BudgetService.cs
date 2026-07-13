using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class BudgetService
{
    private readonly ApplicationDbContext _db;
    private const string TransfersCategoryName = "Transfers";

    public BudgetService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Budget>> GetAllBudgetsAsync()
    {
        return await _db.Budgets
            .Include(b => b.Category)
            .OrderBy(b => b.Category.Name)
            .ToListAsync();
    }

    public async Task<Budget?> GetBudgetAsync(int id)
    {
        return await _db.Budgets
            .Include(b => b.Category)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<Budget?> CreateOrUpdateBudgetAsync(string userId, int categoryId, decimal monthlyAmount)
    {
        // Query filters only scope reads; a posted foreign CategoryId must be rejected here.
        if (!await _db.Categories.AnyAsync(c => c.Id == categoryId)) return null;

        var existing = await _db.Budgets
            .FirstOrDefaultAsync(b => b.CategoryId == categoryId);

        if (existing is not null)
        {
            existing.MonthlyAmount = monthlyAmount;
            await _db.SaveChangesAsync();
            return existing;
        }

        var budget = new Budget
        {
            UserId = userId,
            CategoryId = categoryId,
            MonthlyAmount = monthlyAmount,
            StartMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1)
        };
        _db.Budgets.Add(budget);
        await _db.SaveChangesAsync();
        return budget;
    }

    public async Task<bool> DeleteBudgetAsync(int id)
    {
        var budget = await _db.Budgets.FirstOrDefaultAsync(b => b.Id == id);
        if (budget is null) return false;

        _db.Budgets.Remove(budget);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<BudgetStatus>> GetBudgetStatusAsync(DateOnly month)
    {
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var budgets = await _db.Budgets
            .Include(b => b.Category)
            .Where(b => b.StartMonth <= monthStart && (b.EndMonth == null || b.EndMonth >= monthStart))
            .ToListAsync();

        if (budgets.Count == 0) return new();

        var categoryIds = budgets.Select(b => b.CategoryId).ToList();

        var transactions = await _db.Transactions
            .Include(t => t.Category)
            .Where(t => t.Date >= monthStart && t.Date <= monthEnd && t.Amount < 0)
            .Where(t => t.CategoryId != null && categoryIds.Contains(t.CategoryId.Value))
            .ToListAsync();

        var spendByCategory = transactions
            .GroupBy(t => t.CategoryId!.Value)
            .ToDictionary(g => g.Key, g => Math.Abs(g.Sum(t => t.Amount)));

        return budgets.Select(b =>
        {
            var spent = spendByCategory.GetValueOrDefault(b.CategoryId, 0m);
            var pct = b.MonthlyAmount > 0 ? spent / b.MonthlyAmount : 0m;
            return new BudgetStatus
            {
                Budget = b,
                Spent = spent,
                Remaining = b.MonthlyAmount - spent,
                Percentage = pct
            };
        })
        .OrderByDescending(s => s.Percentage)
        .ToList();
    }

    public async Task<List<BudgetStatus>> GetTopBudgetAlertsAsync(int count = 3)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var statuses = await GetBudgetStatusAsync(today);
        return statuses.Take(count).ToList();
    }
}

public class BudgetStatus
{
    public Budget Budget { get; set; } = null!;
    public decimal Spent { get; set; }
    public decimal Remaining { get; set; }
    public decimal Percentage { get; set; }

    public string StatusClass => Percentage switch
    {
        >= 1m => "danger",
        >= 0.8m => "warning",
        _ => "success"
    };

    public string StatusLabel => Percentage switch
    {
        >= 1m => "Over budget",
        >= 0.8m => "Near limit",
        _ => "On track"
    };
}
