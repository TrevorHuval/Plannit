namespace Plannit.Services.Ai;

/// <summary>
/// A configured AI backend that maps merchant groups to category suggestions. Implementations
/// are provider-specific (Claude CLI, Anthropic API, OpenAI-compatible) but share the prompt
/// and parser so their behavior stays consistent.
/// </summary>
public interface ISmartCategorizer
{
    /// <summary>Human-readable provider name, for logs and the UI.</summary>
    string Name { get; }

    /// <summary>Categorize a batch of merchant groups. Never throws for provider/parse errors —
    /// failures come back as <see cref="SmartCategorizationResult.Failure"/>.</summary>
    Task<SmartCategorizationResult> CategorizeAsync(SmartCategorizationRequest request, CancellationToken ct = default);

    /// <summary>Send a tiny one-merchant probe to confirm the provider is reachable and configured.</summary>
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
}
