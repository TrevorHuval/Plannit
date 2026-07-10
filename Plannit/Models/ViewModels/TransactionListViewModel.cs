using Plannit.Models.Entities;

namespace Plannit.Models.ViewModels;

public class TransactionListViewModel
{
    public List<TransactionRowViewModel> Transactions { get; set; } = new();
    public List<AccountOption> Accounts { get; set; } = new();
    public List<CategoryOption> Categories { get; set; } = new();

    public int? AccountId { get; set; }
    public int? CategoryId { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? SearchText { get; set; }

    public int Page { get; set; } = 1;
    public int TotalPages { get; set; }
}

public class TransactionRowViewModel
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = null!;
    public string AccountName { get; set; } = null!;
    public AccountType AccountType { get; set; }
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
}

public class AccountOption
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}
