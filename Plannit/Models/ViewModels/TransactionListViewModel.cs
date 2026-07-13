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
    public int TotalCount { get; set; }
    public int PageSize { get; set; } = 50;
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
    public string? Notes { get; set; }
    public Guid? SplitGroupId { get; set; }
}

public class AccountOption
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public class SplitTransactionViewModel
{
    public int TransactionId { get; set; }
    public string OriginalDescription { get; set; } = null!;
    public decimal OriginalAmount { get; set; }
    public DateOnly Date { get; set; }
    public List<CategoryOption> Categories { get; set; } = new();
    public List<SplitLineViewModel> Splits { get; set; } = new();
    public string? ReturnUrl { get; set; }
}

public class SplitLineViewModel
{
    public decimal Amount { get; set; }
    public string Description { get; set; } = null!;
    public int? CategoryId { get; set; }
}
