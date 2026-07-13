using System.ComponentModel.DataAnnotations;

namespace Plannit.Models.ViewModels;

public class TransactionFormViewModel
{
    public int? Id { get; set; }

    [Required]
    [Display(Name = "Account")]
    public int AccountId { get; set; }

    [Required]
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    [Required]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(500)]
    public string Description { get; set; } = null!;

    public List<AccountOption> Accounts { get; set; } = new();

    public string? ReturnUrl { get; set; }
}
