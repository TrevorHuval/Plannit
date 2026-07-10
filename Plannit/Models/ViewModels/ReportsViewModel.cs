using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class ReportsViewModel
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string MonthLabel => new DateOnly(Year, Month, 1).ToString("MMMM yyyy");

    public SpendByCategoryResult SpendByCategory { get; set; } = null!;
    public List<MonthlySpend> MonthlyHistory { get; set; } = new();
    public IncomeExpenseSummary IncomeExpense { get; set; } = null!;
    public List<MerchantSpend> TopMerchants { get; set; } = new();
}
