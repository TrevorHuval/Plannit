namespace Plannit.Models.ViewModels;

public class PositionLineViewModel
{
    public string Symbol { get; set; } = null!;
    public string Description { get; set; } = null!;
    public decimal Value { get; set; }

    /// <summary>Share/unit count, when the export reports it (null for cash lines).</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Per-share price ("Last price"), when reported.</summary>
    public decimal? Price { get; set; }

    /// <summary>Total cost basis, when reported.</summary>
    public decimal? CostBasis { get; set; }
}
