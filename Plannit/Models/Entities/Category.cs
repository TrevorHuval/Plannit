namespace Plannit.Models.Entities;

public class Category
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int? ParentId { get; set; }
    public bool IsSystem { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser User { get; set; } = null!;
    public Category? Parent { get; set; }
    public ICollection<Category> Children { get; set; } = new List<Category>();
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public ICollection<CategoryRule> Rules { get; set; } = new List<CategoryRule>();
}
