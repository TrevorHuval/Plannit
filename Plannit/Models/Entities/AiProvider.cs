namespace Plannit.Models.Entities;

/// <summary>
/// Which AI backend a user has configured for Smart Categorization. <see cref="None"/>
/// (the default) hides the feature entirely.
/// </summary>
public enum AiProvider
{
    None = 0,
    ClaudeCli = 1,
    AnthropicApi = 2,
    OpenAiCompatible = 3
}
