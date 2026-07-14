using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public enum NotificationDigestMode
{
    Immediate,
    DailyDigest
}

public class NotificationPreferences
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;

    public string? Email { get; set; }
    public bool EmailEnabled { get; set; }
    public NotificationDigestMode DigestMode { get; set; } = NotificationDigestMode.Immediate;

    public bool BudgetOverageEnabled { get; set; } = true;
    public bool BillDueEnabled { get; set; } = true;
    public bool LowForecastBalanceEnabled { get; set; } = true;
    public bool LargeTransactionEnabled { get; set; } = true;
    public bool StaleAccountEnabled { get; set; } = true;

    public IdentityUser User { get; set; } = null!;
}
