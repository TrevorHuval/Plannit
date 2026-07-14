using System.ComponentModel.DataAnnotations;

namespace Plannit.Models.ViewModels;

public class SavingsGoalCardViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal TargetAmount { get; set; }
    public DateOnly? TargetDate { get; set; }
    public string? LinkedAccountName { get; set; }
    public decimal CurrentAmount { get; set; }
    public decimal Percentage { get; set; }
    public bool IsComplete { get; set; }
    public decimal AmountRemaining { get; set; }
    public DateOnly? ProjectedCompletionDate { get; set; }
}

public class SavingsGoalFormViewModel
{
    public int? Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue, ErrorMessage = "Target amount must be greater than zero.")]
    public decimal TargetAmount { get; set; }

    public DateOnly? TargetDate { get; set; }

    [Display(Name = "Linked Account")]
    public int? LinkedAccountId { get; set; }

    [Display(Name = "Current Progress")]
    [Range(0, double.MaxValue)]
    public decimal? ManualProgress { get; set; }

    public List<AccountOption> LinkableAccounts { get; set; } = new();
}
