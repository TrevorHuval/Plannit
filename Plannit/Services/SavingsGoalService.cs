using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class SavingsGoalService
{
    // Trailing window used to estimate a contribution rate from a linked account's
    // balance history when projecting a completion date.
    private const int ContributionLookbackDays = 90;

    private readonly ApplicationDbContext _db;

    public SavingsGoalService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<SavingsGoal>> GetAllAsync()
    {
        return await _db.SavingsGoals
            .AsNoTracking()
            .Include(g => g.LinkedAccount!).ThenInclude(a => a.Snapshots)
            .OrderBy(g => g.TargetDate ?? DateOnly.MaxValue)
            .ToListAsync();
    }

    public async Task<SavingsGoal?> GetByIdAsync(int id)
    {
        return await _db.SavingsGoals
            .AsNoTracking()
            .Include(g => g.LinkedAccount!).ThenInclude(a => a.Snapshots)
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public async Task<List<Account>> GetLinkableAccountsAsync()
    {
        return await _db.Accounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync();
    }

    public async Task<SavingsGoal> CreateAsync(string userId, string name, decimal targetAmount, DateOnly? targetDate, int? linkedAccountId, decimal? manualProgress)
    {
        var goal = new SavingsGoal
        {
            UserId = userId,
            Name = name,
            TargetAmount = targetAmount,
            TargetDate = targetDate,
            LinkedAccountId = linkedAccountId,
            ManualProgress = linkedAccountId is null ? manualProgress : null
        };
        _db.SavingsGoals.Add(goal);
        await _db.SaveChangesAsync();
        return goal;
    }

    public async Task<bool> UpdateAsync(int id, string name, decimal targetAmount, DateOnly? targetDate, int? linkedAccountId, decimal? manualProgress)
    {
        var goal = await _db.SavingsGoals.FirstOrDefaultAsync(g => g.Id == id);
        if (goal is null) return false;

        goal.Name = name;
        goal.TargetAmount = targetAmount;
        goal.TargetDate = targetDate;
        goal.LinkedAccountId = linkedAccountId;
        goal.ManualProgress = linkedAccountId is null ? manualProgress : null;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var goal = await _db.SavingsGoals.FirstOrDefaultAsync(g => g.Id == id);
        if (goal is null) return false;

        _db.SavingsGoals.Remove(goal);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Computes current progress and a projected completion date for one goal.
    /// Linked-account goals derive both from balance snapshots; manual-progress goals only
    /// report current progress (no history to estimate a contribution rate from).</summary>
    public static SavingsGoalProgress ComputeProgress(SavingsGoal goal, DateOnly today)
    {
        if (goal.LinkedAccount is not null)
        {
            var current = goal.LinkedAccount.Snapshots.OrderByDescending(s => s.Date).FirstOrDefault()?.Balance ?? 0m;
            var cutoff = today.AddDays(-ContributionLookbackDays);
            var priorSnapshot = goal.LinkedAccount.Snapshots
                .Where(s => s.Date <= cutoff)
                .OrderByDescending(s => s.Date)
                .FirstOrDefault();

            DateOnly? projected = null;
            if (priorSnapshot is not null)
            {
                var lookbackDays = today.DayNumber - priorSnapshot.Date.DayNumber;
                projected = ProjectCompletionDate(today, current, goal.TargetAmount, priorSnapshot.Balance, lookbackDays);
            }

            return new SavingsGoalProgress { Goal = goal, CurrentAmount = current, ProjectedCompletionDate = projected };
        }

        return new SavingsGoalProgress { Goal = goal, CurrentAmount = goal.ManualProgress ?? 0m, ProjectedCompletionDate = null };
    }

    /// <summary>Pure projection math: extrapolates the recent linear contribution rate forward
    /// to the target amount. Returns null when the goal is already met, there's no usable
    /// history, or the trend is flat/negative (no forecastable completion).</summary>
    public static DateOnly? ProjectCompletionDate(DateOnly today, decimal currentAmount, decimal targetAmount, decimal amountAtLookbackStart, int lookbackDays)
    {
        if (currentAmount >= targetAmount) return today;
        if (lookbackDays <= 0) return null;

        var dailyRate = (currentAmount - amountAtLookbackStart) / lookbackDays;
        if (dailyRate <= 0) return null;

        var remaining = targetAmount - currentAmount;
        var daysNeeded = remaining / dailyRate;
        if (daysNeeded > 365 * 100) return null;

        return today.AddDays((int)Math.Ceiling(daysNeeded));
    }
}

public class SavingsGoalProgress
{
    public SavingsGoal Goal { get; set; } = null!;
    public decimal CurrentAmount { get; set; }
    public DateOnly? ProjectedCompletionDate { get; set; }

    public decimal Percentage => Goal.TargetAmount > 0 ? Math.Min(CurrentAmount / Goal.TargetAmount, 1m) : 0m;
    public bool IsComplete => CurrentAmount >= Goal.TargetAmount;
    public decimal AmountRemaining => Math.Max(Goal.TargetAmount - CurrentAmount, 0m);
}
