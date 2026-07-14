using System.ComponentModel.DataAnnotations;
using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class NotificationSettingsViewModel
{
    [Display(Name = "Email address")]
    [EmailAddress]
    public string? Email { get; set; }

    [Display(Name = "Send email alerts")]
    public bool EmailEnabled { get; set; }

    [Display(Name = "Delivery")]
    public NotificationDigestMode DigestMode { get; set; } = NotificationDigestMode.Immediate;

    [Display(Name = "Budget overage (crossed 100% this month)")]
    public bool BudgetOverageEnabled { get; set; } = true;

    [Display(Name = "Bill due within 3 days")]
    public bool BillDueEnabled { get; set; } = true;

    [Display(Name = "Cash flow forecast turns negative")]
    public bool LowForecastBalanceEnabled { get; set; } = true;

    [Display(Name = "Unusually large transaction on import")]
    public bool LargeTransactionEnabled { get; set; } = true;

    [Display(Name = "Stale account balances (30+ days)")]
    public bool StaleAccountEnabled { get; set; } = true;

    public bool SmtpConfigured { get; set; }
    public string? TestResult { get; set; }
    public bool TestSucceeded { get; set; }
}
