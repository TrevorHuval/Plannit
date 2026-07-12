namespace Plannit.Models.ViewModels;

public class PendingImportItemViewModel
{
    /// <summary>"CsvMap", "PositionsCsv", or "PdfStatement".</summary>
    public string Kind { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string TempFileId { get; set; } = null!;
    public string TempFileExtension { get; set; } = null!;
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
}
