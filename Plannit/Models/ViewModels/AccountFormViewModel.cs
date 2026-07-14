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

    // Optimistic-concurrency token round-tripped through the edit form.
    public Guid RowVersion { get; set; }
}
