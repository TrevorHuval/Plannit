namespace Plannit.Models.ViewModels;

/// <summary>The review screen: one row per merchant group with the AI's proposal.</summary>
public class SmartCategorizeReviewViewModel
{
    public int? AccountId { get; set; }
    public string ProviderName { get; set; } = "";
    public int UncategorizedCount { get; set; }
    public int GroupCount { get; set; }

    /// <summary>Set when the provider call failed — the view shows the message instead of rows.</summary>
    public string? ProviderError { get; set; }

    public List<SmartCategorizeRowViewModel> Rows { get; set; } = new();

    public bool HasActionableRows => Rows.Any(r => r.HasProposal);
}

public class SmartCategorizeRowViewModel
{
    public string MerchantKey { get; set; } = "";
    public string SampleDescription { get; set; } = "";
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
    public int Sign { get; set; }

    public string? ProposedCategory { get; set; }
    public bool IsNew { get; set; }
    public double Confidence { get; set; }

    /// <summary>Pre-checked for confident proposals; unchecked for low-confidence ones.</summary>
    public bool Accepted { get; set; }
    public bool CreateRule { get; set; } = true;

    public bool HasProposal => !string.IsNullOrWhiteSpace(ProposedCategory);
    public string ConfidencePercent => (Confidence * 100).ToString("0") + "%";
}

/// <summary>POST payload when applying accepted proposals.</summary>
public class SmartCategorizeApplyViewModel
{
    public int? AccountId { get; set; }
    public List<SmartCategorizeApplyRow> Rows { get; set; } = new();
}

public class SmartCategorizeApplyRow
{
    public string MerchantKey { get; set; } = "";
    public string? CategoryName { get; set; }
    public bool IsNew { get; set; }
    public bool Accepted { get; set; }
    public bool CreateRule { get; set; }
}
