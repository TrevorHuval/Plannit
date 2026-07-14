using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

/// <summary>
/// A user's link to an automated bank-sync provider (SimpleFIN). The provider's access URL
/// carries the credentials for polling accounts, so it is stored encrypted at rest via
/// ASP.NET Data Protection — <see cref="AccessUrlProtected"/> holds ciphertext, never the
/// raw URL. One user may hold several connections (e.g. one per SimpleFIN setup token).
/// </summary>
public class SyncConnection
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;

    public SyncProvider Provider { get; set; } = SyncProvider.SimpleFin;

    /// <summary>Friendly label for the connection, shown in the UI.</summary>
    public string Name { get; set; } = "SimpleFIN";

    /// <summary>Data-Protection-encrypted access URL (contains embedded basic-auth credentials).</summary>
    public string AccessUrlProtected { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSyncedAt { get; set; }
    public SyncStatus LastSyncStatus { get; set; } = SyncStatus.Never;
    public string? LastSyncMessage { get; set; }

    /// <summary>False disables automatic (background) syncing without deleting the link.</summary>
    public bool IsActive { get; set; } = true;

    public IdentityUser User { get; set; } = null!;
    public List<SyncAccountMapping> AccountMappings { get; set; } = new();
    public List<SyncLog> Logs { get; set; } = new();
}
