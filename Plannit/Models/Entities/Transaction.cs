namespace Plannit.Models.Entities;

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = null!;
    public string? OriginalDescription { get; set; }
    public int? CategoryId { get; set; }
    public int? ImportBatchId { get; set; }
    public string? ImportHash { get; set; }

    public Account Account { get; set; } = null!;
    public Category? Category { get; set; }
    public ImportBatch? ImportBatch { get; set; }
}
