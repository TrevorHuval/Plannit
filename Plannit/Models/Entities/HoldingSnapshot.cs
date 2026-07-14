namespace Plannit.Models.Entities;

/// <summary>
/// A dated valuation of a <see cref="Holding"/> from a positions statement:
/// share count, per-share price, and total market value on that date.
/// </summary>
public class HoldingSnapshot
{
    public int Id { get; set; }
    public int HoldingId { get; set; }
    public DateOnly Date { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal Value { get; set; }

    public Holding Holding { get; set; } = null!;
}
