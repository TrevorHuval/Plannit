using Microsoft.AspNetCore.Identity;
using Plannit.Services;

namespace Plannit.Models.Entities;

public enum BillSource
{
    Detected,
    Manual
}

public class Bill
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;

    // Normalized via RecurringDetectionService.NormalizeMerchant(Name); used to match
    // imported transactions during reconciliation without depending on exact wording.
    public string MerchantKey { get; set; } = null!;
    public string Name { get; set; } = null!;
    public RecurringCadence Cadence { get; set; }
    public decimal ExpectedAmount { get; set; }
    public DateOnly NextDue { get; set; }
    public bool IsIncome { get; set; }
    public BillSource Source { get; set; }
    public bool IsActive { get; set; } = true;
    public DateOnly? LastPaidDate { get; set; }
    public int? LastPaidTransactionId { get; set; }

    public IdentityUser User { get; set; } = null!;
}
