namespace Plannit.Models.ViewModels;

public class ImportResultViewModel
{
    public string AccountName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public int TotalRows { get; set; }
    public int ImportedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public int CategorizedCount { get; set; }
    public List<ImportRowError> Errors { get; set; } = new();
}

public class ImportRowError
{
    public int RowNumber { get; set; }
    public string Message { get; set; } = null!;
}

public class MultiImportResultViewModel
{
    public string AccountName { get; set; } = null!;
    public List<ImportResultViewModel> FileResults { get; set; } = new();
    public int TotalImported => FileResults.Sum(r => r.ImportedCount);
    public int TotalDuplicates => FileResults.Sum(r => r.DuplicateCount);
    public int TotalErrors => FileResults.Sum(r => r.ErrorCount);
    public int TotalRows => FileResults.Sum(r => r.TotalRows);
    public int CategorizedCount { get; set; }

    public List<CsvPendingMapViewModel> CsvsPendingMapping { get; set; } = new();
}

public class CsvPendingMapViewModel
{
    public string FileName { get; set; } = null!;
    public string TempFileId { get; set; } = null!;
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
}
