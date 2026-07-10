namespace Plannit.Models.ViewModels;

public class ImportResultViewModel
{
    public string AccountName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public int TotalRows { get; set; }
    public int ImportedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public List<ImportRowError> Errors { get; set; } = new();
}

public class ImportRowError
{
    public int RowNumber { get; set; }
    public string Message { get; set; } = null!;
}
