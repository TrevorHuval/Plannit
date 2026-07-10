namespace Plannit.Models.Entities;

public class BalanceSnapshot
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }

    public Account Account { get; set; } = null!;
}
