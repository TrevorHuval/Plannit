namespace Plannit.Models.Entities;

/// <summary>
/// One row per outbound email the app reserved a send for (audit P2-08). Counting rows in a time
/// window gives per-user cooldowns and daily budgets plus a global daily cap, so signed-in users
/// cannot turn the server's SMTP account into a mail cannon. Rows carry no recipient or content.
/// </summary>
public class EmailDispatch
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;

    /// <summary>"UserTriggered" (verification and test mail) or "Alert" (notification mail).</summary>
    public string Kind { get; set; } = null!;

    public DateTime SentUtc { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser User { get; set; } = null!;
}
