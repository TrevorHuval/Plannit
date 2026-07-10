namespace Plannit.Models.Entities;

public class CategoryRule
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public string MatchText { get; set; } = null!;
    public MatchType MatchType { get; set; }
    public int CategoryId { get; set; }
    public int Priority { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser User { get; set; } = null!;
    public Category Category { get; set; } = null!;
}
