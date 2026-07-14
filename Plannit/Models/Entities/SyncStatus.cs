namespace Plannit.Models.Entities;

/// <summary>Outcome of the most recent sync run for a <see cref="SyncConnection"/>.</summary>
public enum SyncStatus
{
    Never = 0,
    Success = 1,
    PartialSuccess = 2,
    Failed = 3,
    /// <summary>The access URL was rejected (401/403) — the user must re-link the connection.</summary>
    TokenExpired = 4
}
