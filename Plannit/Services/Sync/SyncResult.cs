namespace Plannit.Services.Sync;

/// <summary>Summary of one sync run, surfaced to the UI and persisted as a <see cref="Models.Entities.SyncLog"/>.</summary>
public class SyncResult
{
    public bool Success { get; set; }
    /// <summary>The provider rejected the credentials — the connection needs re-linking.</summary>
    public bool TokenExpired { get; set; }

    public int AccountsSynced { get; set; }
    public int UnmappedAccounts { get; set; }
    public int TransactionsImported { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int SnapshotsUpdated { get; set; }

    public string Message { get; set; } = "";
}
