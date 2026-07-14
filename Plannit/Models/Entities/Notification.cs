using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public enum NotificationType
{
    BudgetOverage,
    BillDue,
    LowForecastBalance,
    LargeTransaction,
    StaleAccount
}

public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public NotificationType Type { get; set; }
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? Url { get; set; }

    // Identifies the specific alert instance (e.g. "BudgetOverage:12:2026-07") so the alert
    // engine can skip re-creating a notification for a condition that's still true, without
    // needing separate "last alerted" tracking per rule.
    public string DedupKey { get; set; } = null!;

    public bool IsRead { get; set; }
    public bool EmailSent { get; set; }
    public DateTime CreatedUtc { get; set; }

    public IdentityUser User { get; set; } = null!;
}
