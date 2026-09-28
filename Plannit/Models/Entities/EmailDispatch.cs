namespace Plannit.Models.Entities;

/// <summary>
/// One row per outbound email the app reserved a send for (audit P2-08). Counting rows in a time
/// window gives per-user cooldowns and daily budgets plus a global daily cap, so signed-in users
/// cannot turn the server's SMTP account into a mail cannon. Rows carry no recipient or content.
/// </summary>
public class EmailDispatch
{
    public int Id { get; set; }
    /// <summary>Null for account mail (registration, resend, password reset) sent before any user is signed in.</summary>
    public string? UserId { get; set; }

    /// <summary>SHA-256 of the lower-cased recipient, so per-address limits work without storing addresses.</summary>
    public string? RecipientHash { get; set; }

    /// <summary>"UserTriggered" (verification and test mail), "Alert" (notification mail) or "Account" (Identity mail).</summary>
    public string Kind { get; set; } = null!;

    public DateTime SentUtc { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser? User { get; set; }
}
