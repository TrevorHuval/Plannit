using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public class Account
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public AccountType Type { get; set; }
    public string? Institution { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Optimistic-concurrency token; re-stamped on every tracked save (see ApplicationDbContext).
    public Guid RowVersion { get; set; }

    public IdentityUser User { get; set; } = null!;
    public ICollection<BalanceSnapshot> Snapshots { get; set; } = new List<BalanceSnapshot>();
}
