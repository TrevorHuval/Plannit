using System.ComponentModel.DataAnnotations;
using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class AccountFormViewModel
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = null!;

    [Required]
    [Display(Name = "Account Type")]
    public AccountType Type { get; set; }

    [StringLength(100)]
    public string? Institution { get; set; }

    // Loan/Mortgage only — entered as a whole percent (e.g. 6.5) and converted to a fraction on save.
    [Display(Name = "Interest Rate (APR %)")]
    [Range(0, 100)]
    public decimal? InterestRatePercent { get; set; }

    [Display(Name = "Minimum Payment")]
    [Range(0, double.MaxValue)]
    public decimal? MinimumPayment { get; set; }

    [Display(Name = "Original Principal")]
    [Range(0, double.MaxValue)]
    public decimal? OriginalPrincipal { get; set; }

    // Optimistic-concurrency token round-tripped through the edit form.
    public Guid RowVersion { get; set; }
}
