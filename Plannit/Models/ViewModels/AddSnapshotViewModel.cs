using System.ComponentModel.DataAnnotations;

namespace Plannit.Models.ViewModels;

public class AddSnapshotViewModel
{
    public int AccountId { get; set; }

    [Required]
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    [Required]
    [DataType(DataType.Currency)]
    public decimal Balance { get; set; }
}
