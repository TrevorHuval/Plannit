using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class ReportsViewModel
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Preset { get; set; }

    public string DateRangeLabel
    {
        get
        {
            if (StartDate == DateOnly.MinValue || EndDate == DateOnly.MaxValue)
                return "All Time";
            if (StartDate == EndDate)
                return StartDate.ToString("MMMM d, yyyy");
            if (StartDate.Year == EndDate.Year)
                return $"{StartDate.ToString("MMM d")} – {EndDate.ToString("MMM d, yyyy")}";
            return $"{StartDate.ToString("MMM d, yyyy")} – {EndDate.ToString("MMM d, yyyy")}";
        }
    }

    public SpendByCategoryResult SpendByCategory { get; set; } = null!;
    public List<MonthlySpend> MonthlyHistory { get; set; } = new();
    public IncomeExpenseSummary IncomeExpense { get; set; } = null!;
    public List<MerchantSpend> TopMerchants { get; set; } = new();
    public TransfersSanityResult TransfersSanity { get; set; } = null!;
}
