namespace Plannit.Models.Entities;

public class ProjectionAccountAssumption
{
    public int Id { get; set; }
    public int ScenarioId { get; set; }
    public int AccountId { get; set; }
    public decimal AnnualContribution { get; set; }
    public decimal EmployerMatch { get; set; }
    public decimal ExpectedReturnRate { get; set; }
    public int ContributionEndAge { get; set; }

    public ProjectionScenario Scenario { get; set; } = null!;
    public Account Account { get; set; } = null!;
}
