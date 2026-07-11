using Microsoft.AspNetCore.Identity;

namespace Plannit.Models.Entities;

public class ProjectionScenario
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int BirthYear { get; set; }
    public int RetirementAge { get; set; }
    public int LifeExpectancy { get; set; }
    public decimal AnnualRetirementSpending { get; set; }
    public decimal InflationRate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IdentityUser User { get; set; } = null!;
    public ICollection<ProjectionAccountAssumption> AccountAssumptions { get; set; } = new List<ProjectionAccountAssumption>();
}
