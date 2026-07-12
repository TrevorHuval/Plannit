using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class DashboardViewModel
{
    public decimal NetWorth { get; set; }
    public required List<TypeTotalViewModel> TypeTotals { get; set; }
    public required List<NetWorthPointViewModel> NetWorthHistory { get; set; }
    public bool HasAccounts { get; set; }
    public List<BudgetStatus> BudgetAlerts { get; set; } = new();
    public List<RecurringGroup> UpcomingRecurring { get; set; } = new();
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
