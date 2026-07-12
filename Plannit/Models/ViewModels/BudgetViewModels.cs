using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class BudgetIndexViewModel
{
    public DateOnly Month { get; set; }
    public string MonthLabel => Month.ToString("MMMM yyyy");
    public List<BudgetStatus> Statuses { get; set; } = new();
    public decimal TotalBudgeted => Statuses.Sum(s => s.Budget.MonthlyAmount);
    public decimal TotalSpent => Statuses.Sum(s => s.Spent);
}

public class BudgetSettingsViewModel
{
    public List<BudgetCategoryRow> Categories { get; set; } = new();
}

public class BudgetCategoryRow
{
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = null!;
    public decimal? MonthlyAmount { get; set; }
    public int? BudgetId { get; set; }
}

public class RecurringIndexViewModel
{
    public List<RecurringGroup> RecurringGroups { get; set; } = new();
    public decimal AnnualizedTotal => RecurringGroups.Sum(g => g.AnnualizedCost);
}
