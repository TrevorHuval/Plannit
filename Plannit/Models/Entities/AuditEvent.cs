namespace Plannit.Models.Entities;

public class AuditEvent
{
    public int Id { get; set; }
    public string? UserId { get; set; }
    public string Action { get; set; } = null!;
    public string? Detail { get; set; }
    public DateTime Utc { get; set; }
    public string? Ip { get; set; }
}
