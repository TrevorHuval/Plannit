using System.ComponentModel.DataAnnotations;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Models.ViewModels;

public class ScenarioListViewModel
{
    public List<ScenarioSummary> Scenarios { get; set; } = [];
}

public class ScenarioSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int RetirementAge { get; set; }
    public int LifeExpectancy { get; set; }
    public decimal AnnualRetirementSpending { get; set; }
    public int AccountCount { get; set; }
}

public class ScenarioFormViewModel
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "My Retirement Plan";

    [Required, Range(1900, 2100)]
    public int BirthYear { get; set; }

    [Required, Range(30, 100)]
    public int RetirementAge { get; set; } = 65;

    [Required, Range(50, 120)]
    public int LifeExpectancy { get; set; } = 90;

    [Required, Range(0, 10_000_000)]
    [Display(Name = "Annual Retirement Spending")]
    public decimal AnnualRetirementSpending { get; set; } = 50_000;

    [Required, Range(0, 0.2)]
    [Display(Name = "Inflation Rate")]
    public decimal InflationRate { get; set; } = 0.03m;

    [Required, Range(0, 1)]
    [Display(Name = "Return Std Dev")]
    public decimal ReturnStdDev { get; set; } = 0.15m;

    public List<AccountAssumptionFormItem> Accounts { get; set; } = [];
    public List<LifeEventFormItem> Events { get; set; } = [];
}

public class AccountAssumptionFormItem
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public AccountType AccountType { get; set; }
    public decimal CurrentBalance { get; set; }

    [Range(0, 10_000_000)]
    public decimal AnnualContribution { get; set; }

    [Range(0, 10_000_000)]
    public decimal EmployerMatch { get; set; }

    [Range(0, 1)]
    public decimal ExpectedReturnRate { get; set; } = 0.07m;

    [Range(0, 120)]
    public int ContributionEndAge { get; set; } = 65;
}

public class LifeEventFormItem
{
    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [Required, Range(0, 120)]
    public int Age { get; set; }

    [Required]
    public decimal Amount { get; set; }

    public bool IsRecurring { get; set; }

    [Range(0, 120)]
    public int? EndAge { get; set; }
}

public class ScenarioResultsViewModel
{
    public int ScenarioId { get; set; }
    public string ScenarioName { get; set; } = "";
    public int RetirementAge { get; set; }
    public int LifeExpectancy { get; set; }
    public decimal AnnualRetirementSpending { get; set; }
    public ProjectionResult Result { get; set; } = null!;
    public MonteCarloResult MonteCarlo { get; set; } = null!;
    public FireResult Fire { get; set; } = null!;
    public List<AccountInfo> Accounts { get; set; } = [];
    public List<LifeEventInput> LifeEvents { get; set; } = [];
}

public class AccountInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
}

public class ScenarioCompareViewModel
{
    public List<CompareItem> Items { get; set; } = [];
}

public class CompareItem
{
    public int ScenarioId { get; set; }
    public string ScenarioName { get; set; } = "";
    public int RetirementAge { get; set; }
    public decimal RetirementBalance { get; set; }
    public int? DepletionAge { get; set; }
    public decimal SurplusAtDeath { get; set; }
    public decimal SafeSpendingEstimate { get; set; }
    public decimal SuccessProbability { get; set; }
    public decimal FireNumber { get; set; }
    public int? ProjectedFireAge { get; set; }
}
