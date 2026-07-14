namespace Plannit.Models.ViewModels;

public class ImportResultViewModel
{
    public string AccountName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public int TotalRows { get; set; }
    public int ImportedCount { get; set; }
    public int? ImportBatchId { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public int CategorizedCount { get; set; }
    public List<ImportRowError> Errors { get; set; } = new();

    public bool SnapshotOnly { get; set; }
    public bool SnapshotUpdated { get; set; }
    public decimal? SnapshotBalance { get; set; }
    public DateOnly? SnapshotDate { get; set; }

    /// <summary>Number of holdings upserted from a positions export (0 for other files).</summary>
    public int HoldingsUpdated { get; set; }
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
    public int SnapshotsUpdated => FileResults.Count(r => r.SnapshotUpdated);
    public int CategorizedCount { get; set; }

    public bool ShowSnapshotNudge { get; set; }
    public int NudgeAccountId { get; set; }
    public DateOnly? NudgeLatestSnapshotDate { get; set; }
    public DateOnly NudgeNewestTransactionDate { get; set; }

    public int AccountId { get; set; }
    public bool AiConfigured { get; set; }
    public int UncategorizedRemaining { get; set; }
}
