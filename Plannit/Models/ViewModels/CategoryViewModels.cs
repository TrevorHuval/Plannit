using System.ComponentModel.DataAnnotations;
using MatchType = Plannit.Models.Entities.MatchType;

namespace Plannit.Models.ViewModels;

public class CategoryListViewModel
{
    public List<CategoryItemViewModel> Categories { get; set; } = new();
}

public class CategoryItemViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? ParentName { get; set; }
    public bool IsSystem { get; set; }
    public int RuleCount { get; set; }
    public int TransactionCount { get; set; }
}

public class CategoryFormViewModel
{
    public int? Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = null!;

    public int? ParentId { get; set; }

    public List<CategoryOption> AvailableParents { get; set; } = new();
}

public class CategoryOption
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public class CategoryRuleListViewModel
{
    public List<CategoryRuleItemViewModel> Rules { get; set; } = new();
}

public class CategoryRuleItemViewModel
{
    public int Id { get; set; }
    public string MatchText { get; set; } = null!;
    public MatchType MatchType { get; set; }
    public string CategoryName { get; set; } = null!;
    public int Priority { get; set; }
}

public class CategoryRuleFormViewModel
{
    public int? Id { get; set; }

    [Required]
    [StringLength(200)]
    public string MatchText { get; set; } = null!;

    public MatchType MatchType { get; set; }

    [Required]
    public int CategoryId { get; set; }

    public int Priority { get; set; } = 50;

    public List<CategoryOption> Categories { get; set; } = new();
}

public class CreateRuleFromTransactionViewModel
{
    public int TransactionId { get; set; }
    public string Description { get; set; } = null!;

    [Required]
    [StringLength(200)]
    public string MatchText { get; set; } = null!;

    public MatchType MatchType { get; set; } = MatchType.Contains;

    [Required]
    public int CategoryId { get; set; }

    public int Priority { get; set; } = 50;

    public List<CategoryOption> Categories { get; set; } = new();
    public string? ReturnUrl { get; set; }
}

public class MergeCategoryViewModel
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = null!;

    [Required]
    public int TargetId { get; set; }

    public List<CategoryOption> AvailableTargets { get; set; } = new();
}

public class RuleTestViewModel
{
    public string? MatchText { get; set; }
    public string MatchType { get; set; } = "Contains";
    public List<CategoryOption> Categories { get; set; } = new();
    public List<TransactionMatchViewModel> MatchingTransactions { get; set; } = new();
}

public class TransactionMatchViewModel
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public string AccountName { get; set; } = null!;
    public string? CurrentCategory { get; set; }
}
