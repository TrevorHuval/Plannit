using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public class SavingsGoal
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public decimal TargetAmount { get; set; }
    public DateOnly? TargetDate { get; set; }
    public int? LinkedAccountId { get; set; }

    // Only meaningful when LinkedAccountId is null — progress entered by hand.
    public decimal? ManualProgress { get; set; }

    public IdentityUser User { get; set; } = null!;
    public Account? LinkedAccount { get; set; }
}
