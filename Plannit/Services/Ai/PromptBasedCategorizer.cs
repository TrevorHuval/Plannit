namespace Plannit.Services.Ai;

/// <summary>
/// Shared plumbing for prompt-driven providers: builds the prompt, hands it to the concrete
/// backend's <see cref="CompleteAsync"/>, and parses the reply. Concrete providers only need
/// to know how to turn a system+user prompt into raw text.
/// </summary>
public abstract class PromptBasedCategorizer : ISmartCategorizer
{
    public abstract string Name { get; }

    /// <summary>Send the prompt to the backend and return its raw text reply.</summary>
    protected abstract Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);

    public async Task<SmartCategorizationResult> CategorizeAsync(SmartCategorizationRequest request, CancellationToken ct = default)
    {
        if (request.Groups.Count == 0)
            return SmartCategorizationResult.Ok(Array.Empty<CategoryProposal>());

        var (system, user) = SmartCategorizationPrompt.Build(request);

        string raw;
        try
        {
            raw = await CompleteAsync(system, user, ct);
        }
        catch (OperationCanceledException)
        {
            return SmartCategorizationResult.Failure($"{Name} timed out. Try again or reduce the number of transactions.");
        }
        catch (Exception ex)
        {
            return SmartCategorizationResult.Failure($"{Name} request failed: {ex.Message}");
        }

        var proposals = SmartCategorizationResponseParser.Parse(raw, request);
        return SmartCategorizationResult.Ok(proposals);
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var probe = new SmartCategorizationRequest(
            new[] { new MerchantGroup("STARBUCKS", new[] { "STARBUCKS STORE 1234" }, 3, -6.25m, -1) },
            new[] { "Dining", "Groceries", "Transport" },
            SmartCategorizationPrompt.MaxCategories);

        var (system, user) = SmartCategorizationPrompt.Build(probe);
        try
        {
            var raw = await CompleteAsync(system, user, ct);
            var proposals = SmartCategorizationResponseParser.Parse(raw, probe);
            return proposals.Count > 0
                ? (true, $"Connected to {Name}. The model responded with a valid suggestion.")
                : (true, $"Connected to {Name}, but the response could not be parsed as a suggestion. Categorization may be unreliable.");
        }
        catch (OperationCanceledException)
        {
            return (false, $"{Name} timed out.");
        }
        catch (Exception ex)
        {
            return (false, $"{Name} connection failed: {ex.Message}");
        }
    }
}
