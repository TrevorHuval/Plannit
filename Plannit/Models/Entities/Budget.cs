using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public class Budget
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int CategoryId { get; set; }
    public decimal MonthlyAmount { get; set; }
    public DateOnly StartMonth { get; set; }
    public DateOnly? EndMonth { get; set; }

    public IdentityUser User { get; set; } = null!;
    public Category Category { get; set; } = null!;
}
