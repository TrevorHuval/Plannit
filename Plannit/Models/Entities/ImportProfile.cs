namespace Plannit.Models.Entities;

public class ImportProfile
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string DateColumn { get; set; } = null!;
    public string DateFormat { get; set; } = null!;
    public string? AmountColumn { get; set; }
    public string? DebitColumn { get; set; }
    public string? CreditColumn { get; set; }
    public string DescriptionColumn { get; set; } = null!;
    public bool InvertAmounts { get; set; }

    public Account Account { get; set; } = null!;
}
