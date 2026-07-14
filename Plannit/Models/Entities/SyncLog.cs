namespace Plannit.Models.Entities;

/// <summary>One row per sync run for a <see cref="SyncConnection"/> — the per-connection sync history.</summary>
public class SyncLog
{
    public int Id { get; set; }
    public int SyncConnectionId { get; set; }

    public DateTime Utc { get; set; } = DateTime.UtcNow;

    public bool Success { get; set; }

    public int TransactionsImported { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int SnapshotsUpdated { get; set; }

    /// <summary>Human-readable summary or error detail for this run.</summary>
    public string? Message { get; set; }

    public SyncConnection SyncConnection { get; set; } = null!;
}
