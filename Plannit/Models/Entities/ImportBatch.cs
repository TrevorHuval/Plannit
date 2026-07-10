namespace Plannit.Models.Entities;

public class ImportBatch
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string FileName { get; set; } = null!;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public int RowCount { get; set; }

    public Account Account { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
