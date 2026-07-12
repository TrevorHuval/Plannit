namespace Plannit.Models.ViewModels;

public class PositionLineViewModel
{
    public string Symbol { get; set; } = null!;
    public string Description { get; set; } = null!;
    public decimal Value { get; set; }
}
