namespace Plannit.Services.Ai;

/// <summary>
/// A cluster of uncategorized transactions sharing a normalized merchant key. This is the
/// unit the AI reasons about — one decision per merchant, not per transaction.
/// </summary>
public record MerchantGroup(
    string MerchantKey,
    IReadOnlyList<string> SampleDescriptions,
    int Count,
    decimal AvgAmount,
    int Sign);

/// <summary>What the AI proposes for a single merchant group. Null category = leave untouched.</summary>
public record CategoryProposal(
    string MerchantKey,
    string? CategoryName,
    double Confidence,
    bool IsNewCategorySuggestion);

/// <summary>Everything a provider needs to build its prompt.</summary>
public record SmartCategorizationRequest(
    IReadOnlyList<MerchantGroup> Groups,
    IReadOnlyList<string> ExistingCategories,
    int MaxCategories)
{
    public int CategoryCount => ExistingCategories.Count;
    public bool AtCategoryCap => CategoryCount >= MaxCategories;
}

/// <summary>Provider output: proposals plus a success flag so callers can degrade gracefully.</summary>
public record SmartCategorizationResult(
    IReadOnlyList<CategoryProposal> Proposals,
    bool Success,
    string? ErrorMessage)
{
    public static SmartCategorizationResult Ok(IReadOnlyList<CategoryProposal> proposals) =>
        new(proposals, true, null);

    public static SmartCategorizationResult Failure(string message) =>
        new(Array.Empty<CategoryProposal>(), false, message);
}

/// <summary>Decrypted, runtime provider configuration built from a user's <c>AiSettings</c> row.</summary>
public record AiProviderConfig(string? Endpoint, string? Model, string? ApiKey);
