using System.ComponentModel.DataAnnotations;

namespace Plannit.Models.ViewModels;

public class ImportMapViewModel
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string TempFileId { get; set; } = null!;

    public List<string> AvailableColumns { get; set; } = new();
    public List<List<string>> PreviewRows { get; set; } = new();

    [Required]
    [Display(Name = "Date Column")]
    public string DateColumn { get; set; } = null!;

    [Required]
    [Display(Name = "Date Format")]
    public string DateFormat { get; set; } = "MM/dd/yyyy";

    [Display(Name = "Amount Column (single column, signed)")]
    public string? AmountColumn { get; set; }

    [Display(Name = "Debit Column")]
    public string? DebitColumn { get; set; }

    [Display(Name = "Credit Column")]
    public string? CreditColumn { get; set; }

    [Required]
    [Display(Name = "Description Column")]
    public string DescriptionColumn { get; set; } = null!;

    [Display(Name = "This export shows charges as positive — flip signs")]
    public bool InvertAmounts { get; set; }
}
