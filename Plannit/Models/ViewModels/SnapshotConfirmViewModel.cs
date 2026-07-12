using System.ComponentModel.DataAnnotations;

namespace Plannit.Models.ViewModels;

public class SnapshotConfirmViewModel
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string TempFileId { get; set; } = null!;
    public string TempFileExtension { get; set; } = null!;

    /// <summary>"PositionsCsv" or "PdfStatement".</summary>
    public string SourceType { get; set; } = null!;

    [Required]
    [Display(Name = "As-Of Date")]
    public DateOnly AsOfDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    [Required]
    public decimal Balance { get; set; }

    public bool BalanceFound { get; set; } = true;
    public bool DateFound { get; set; } = true;

    public List<PositionLineViewModel> Positions { get; set; } = new();
    public string? ExtractedText { get; set; }
}
