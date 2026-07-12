namespace Plannit.Models.Entities;

public class ProjectionEvent
{
    public int Id { get; set; }
    public int ScenarioId { get; set; }
    public string Name { get; set; } = null!;
    public int Age { get; set; }
    public decimal Amount { get; set; }
    public bool IsRecurring { get; set; }
    public int? EndAge { get; set; }

    public ProjectionScenario Scenario { get; set; } = null!;
}
