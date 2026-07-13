namespace Plannit.Models.Entities;

/// <summary>
/// Per-user configuration for the Smart Categorization AI provider. One row per user.
/// The API key is stored encrypted at rest via ASP.NET Data Protection — never in plain
/// text — so <see cref="ApiKeyProtected"/> holds ciphertext, not the raw secret.
/// </summary>
public class AiSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;

    public AiProvider Provider { get; set; } = AiProvider.None;

    /// <summary>Base URL for the OpenAI-compatible provider (e.g. http://localhost:11434/v1).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model identifier (e.g. claude-haiku-4-5, gpt-4o-mini, llama3.1).</summary>
    public string? Model { get; set; }

    /// <summary>Data-Protection-encrypted API key. Null for the Claude CLI provider (no key needed).</summary>
    public string? ApiKeyProtected { get; set; }

    public Microsoft.AspNetCore.Identity.IdentityUser User { get; set; } = null!;
}
