namespace Plannit.Models.Entities;

/// <summary>
/// A security position held in an investment account (one row per account+symbol).
/// Current Quantity/CostBasis reflect the most recent positions import; the dated
/// history lives in <see cref="HoldingSnapshot"/>.
/// </summary>
public class Holding
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>Latest known share/unit count (null for cash/money-market lines with no quantity).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Latest known total cost basis, when the statement reports it.</summary>
    public decimal? CostBasis { get; set; }

    public Account Account { get; set; } = null!;
    public ICollection<HoldingSnapshot> Snapshots { get; set; } = new List<HoldingSnapshot>();
}
