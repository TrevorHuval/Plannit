using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class AccountListViewModel
{
    public required List<AccountGroupViewModel> Groups { get; set; }
}

public class AccountGroupViewModel
{
    public AccountType Type { get; set; }
    public string TypeDisplayName { get; set; } = null!;
    public required List<AccountSummaryViewModel> Accounts { get; set; }
}

public class AccountSummaryViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Institution { get; set; }
    public AccountType Type { get; set; }
    public decimal? LatestBalance { get; set; }
    public DateOnly? LatestSnapshotDate { get; set; }
}
