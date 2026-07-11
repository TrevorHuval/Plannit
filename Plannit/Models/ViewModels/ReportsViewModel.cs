using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class ReportsViewModel
{
    public DateOnly SelectedDate { get; set; }
    public string DateLabel => SelectedDate.ToString("MMMM d, yyyy");

    public SpendByCategoryResult SpendByCategory { get; set; } = null!;
    public List<MonthlySpend> MonthlyHistory { get; set; } = new();
    public IncomeExpenseSummary IncomeExpense { get; set; } = null!;
    public List<MerchantSpend> TopMerchants { get; set; } = new();
}
