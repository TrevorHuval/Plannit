namespace Plannit.Models.Entities;

/// <summary>
/// Links one provider-side account (a SimpleFIN account id) to a Plannit <see cref="Account"/>.
/// A null <see cref="AccountId"/> means the external account is known but not yet mapped, so it
/// is skipped during sync until the user links it.
/// </summary>
public class SyncAccountMapping
{
    public int Id { get; set; }
    public int SyncConnectionId { get; set; }

    /// <summary>Provider-side account identifier (SimpleFIN <c>account.id</c>) — stable dedup anchor.</summary>
    public string ExternalAccountId { get; set; } = null!;

    /// <summary>Provider-side account name, for display on the mapping screen.</summary>
    public string? ExternalAccountName { get; set; }

    /// <summary>Provider-side organization/institution name, for display.</summary>
    public string? ExternalOrgName { get; set; }

    /// <summary>Mapped Plannit account, or null when the external account is left unlinked.</summary>
    public int? AccountId { get; set; }

    public SyncConnection SyncConnection { get; set; } = null!;
    public Account? Account { get; set; }
}
