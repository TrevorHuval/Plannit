using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class AccountDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public AccountType Type { get; set; }
    public string TypeDisplayName { get; set; } = null!;
    public string? Institution { get; set; }
    public bool IsActive { get; set; }
    public required List<SnapshotViewModel> Snapshots { get; set; }

    /// <summary>Whether this account holds securities (investment types) — drives the holdings section.</summary>
    public bool IsInvestment { get; set; }

    /// <summary>Per-holding detail; null/empty for non-investment accounts or before any positions import.</summary>
    public AccountHoldingsViewModel? Holdings { get; set; }
}

public class SnapshotViewModel
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }
}
