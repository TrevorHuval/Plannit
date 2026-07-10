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
}

public class SnapshotViewModel
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }
}
