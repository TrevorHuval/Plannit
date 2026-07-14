using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class DebtAccountSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string TypeDisplayName { get; set; } = null!;
    public decimal Balance { get; set; }
    public decimal? InterestRate { get; set; }
    public decimal? MinimumPayment { get; set; }

    public bool HasPayoffData => InterestRate.HasValue && MinimumPayment.HasValue && MinimumPayment > 0;
}

public class DebtIndexViewModel
{
    public List<DebtAccountSummary> Accounts { get; set; } = new();
    public decimal TotalDebt => Accounts.Sum(a => a.Balance);
    public decimal TotalMinimumPayment => Accounts.Sum(a => a.MinimumPayment ?? 0);
}

public class AmortizationViewModel
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
    public decimal Principal { get; set; }
    public decimal AnnualRate { get; set; }
    public decimal MonthlyPayment { get; set; }
    public DateOnly StartDate { get; set; }
    public List<AmortizationRow> Schedule { get; set; } = new();

    public decimal TotalInterest => Schedule.Sum(r => r.Interest);
    public DateOnly PayoffDate => StartDate.AddMonths(Schedule.Count);
}

public class DebtCompareViewModel
{
    public List<DebtAccountSummary> IncludedAccounts { get; set; } = new();
    public List<DebtAccountSummary> ExcludedAccounts { get; set; } = new();
    public decimal ExtraPayment { get; set; }
    public DebtPayoffComparison? Comparison { get; set; }
}
