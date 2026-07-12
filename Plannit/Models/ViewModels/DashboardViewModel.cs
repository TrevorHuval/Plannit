using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class DashboardViewModel
{
    public decimal NetWorth { get; set; }
    public decimal NetWorth1MonthAgo { get; set; }
    public decimal NetWorth12MonthsAgo { get; set; }
    public required List<TypeTotalViewModel> TypeTotals { get; set; }
    public required List<NetWorthPointViewModel> NetWorthHistory { get; set; }
    public bool HasAccounts { get; set; }
    public List<BudgetStatus> BudgetAlerts { get; set; } = new();
    public List<RecurringGroup> UpcomingRecurring { get; set; } = new();
    public decimal ThisMonthIncome { get; set; }
    public decimal ThisMonthSpending { get; set; }
    public decimal ThisMonthBudgetTotal { get; set; }
    public List<CategorySpend> TopCategories { get; set; } = new();
    public List<RecentTransactionViewModel> RecentTransactions { get; set; } = new();
    public List<CategoryOption> Categories { get; set; } = new();
    public List<StaleAccountViewModel> StaleAccounts { get; set; } = new();

    public decimal NetWorth1MonthDelta => NetWorth1MonthAgo != 0
        ? (NetWorth - NetWorth1MonthAgo) / Math.Abs(NetWorth1MonthAgo)
        : 0;

    public decimal NetWorth12MonthDelta => NetWorth12MonthsAgo != 0
        ? (NetWorth - NetWorth12MonthsAgo) / Math.Abs(NetWorth12MonthsAgo)
        : 0;
}

public class TypeTotalViewModel
{
    public AccountType Type { get; set; }
    public string TypeDisplayName { get; set; } = null!;
    public decimal Total { get; set; }
    public bool IsLiability { get; set; }
}

public class NetWorthPointViewModel
{
    public string Date { get; set; } = null!;
    public decimal NetWorth { get; set; }
}

public class RecentTransactionViewModel
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string AccountName { get; set; } = null!;
}

public class StaleAccountViewModel
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
    public int DaysSinceUpdate { get; set; }
}
