namespace Plannit.Models.ViewModels;

/// <summary>One security row on an account's holdings table.</summary>
public class HoldingViewModel
{
    public int Id { get; set; }
    public string Symbol { get; set; } = null!;
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal Value { get; set; }
    public decimal? CostBasis { get; set; }

    /// <summary>Value − CostBasis when cost basis is known; otherwise null.</summary>
    public decimal? GainLoss => CostBasis.HasValue ? Value - CostBasis.Value : null;

    /// <summary>Gain/loss as a fraction of cost basis; null when cost basis is missing or zero.</summary>
    public decimal? GainLossPercent =>
        CostBasis.HasValue && CostBasis.Value != 0 ? (Value - CostBasis.Value) / CostBasis.Value : null;

    /// <summary>Share of the account's total holdings value (0–1).</summary>
    public decimal Weight { get; set; }
}

/// <summary>Holdings detail for a single investment account (detail page section).</summary>
public class AccountHoldingsViewModel
{
    public decimal TotalValue { get; set; }
    public DateOnly? AsOfDate { get; set; }
    public List<HoldingViewModel> Holdings { get; set; } = new();

    /// <summary>Per-holding value history for the small line chart (symbol → dated values).</summary>
    public List<HoldingHistorySeries> History { get; set; } = new();

    public bool HasHoldings => Holdings.Count > 0;
}

public class HoldingHistorySeries
{
    public string Symbol { get; set; } = null!;
    public List<HoldingHistoryPoint> Points { get; set; } = new();
}

public class HoldingHistoryPoint
{
    public DateOnly Date { get; set; }
    public decimal Value { get; set; }
}

/// <summary>Portfolio-wide allocation aggregated by symbol across all investment accounts.</summary>
public class PortfolioViewModel
{
    public decimal TotalValue { get; set; }
    public List<PortfolioPositionViewModel> Positions { get; set; } = new();
    public bool HasHoldings => Positions.Count > 0;

    /// <summary>Positions exceeding the single-position concentration threshold.</summary>
    public List<PortfolioPositionViewModel> ConcentrationWarnings =>
        Positions.Where(p => p.IsConcentrated).ToList();

    public const decimal ConcentrationThreshold = 0.20m;
}

public class PortfolioPositionViewModel
{
    public string Symbol { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Value { get; set; }
    public decimal? CostBasis { get; set; }
    public decimal Weight { get; set; }

    /// <summary>Names of the accounts this symbol is held in.</summary>
    public List<string> Accounts { get; set; } = new();

    public decimal? GainLoss => CostBasis.HasValue ? Value - CostBasis.Value : null;
    public decimal? GainLossPercent =>
        CostBasis.HasValue && CostBasis.Value != 0 ? (Value - CostBasis.Value) / CostBasis.Value : null;

    public bool IsConcentrated => Weight > PortfolioViewModel.ConcentrationThreshold;
}
