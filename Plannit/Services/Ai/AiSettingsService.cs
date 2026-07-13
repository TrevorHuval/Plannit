using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services.Ai;

/// <summary>
/// Owns the per-user AI provider configuration: reads/writes the <see cref="AiSettings"/> row,
/// encrypts the API key at rest via Data Protection, and constructs the right
/// <see cref="ISmartCategorizer"/> for the configured provider.
/// </summary>
public class AiSettingsService
{
    private const string ProtectorPurpose = "Plannit.AiSettings.ApiKey";

    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClaudeCliStatus _cliStatus;

    public AiSettingsService(
        ApplicationDbContext db,
        IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClientFactory,
        ClaudeCliStatus cliStatus)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _httpClientFactory = httpClientFactory;
        _cliStatus = cliStatus;
    }

    public bool ClaudeCliAvailable => _cliStatus.Available;
    public string? ClaudeCliVersion => _cliStatus.Version;

    public Task<AiSettings?> GetAsync() =>
        _db.AiSettings.FirstOrDefaultAsync();

    /// <summary>True when a usable provider is configured (and, for the CLI, that it's present).</summary>
    public async Task<bool> IsConfiguredAsync()
    {
        var settings = await GetAsync();
        return settings is not null && IsUsable(settings);
    }

    private bool IsUsable(AiSettings s) => s.Provider switch
    {
        AiProvider.None => false,
        AiProvider.ClaudeCli => _cliStatus.Available,
        AiProvider.AnthropicApi => !string.IsNullOrEmpty(s.ApiKeyProtected),
        AiProvider.OpenAiCompatible => !string.IsNullOrWhiteSpace(s.Endpoint) && !string.IsNullOrWhiteSpace(s.Model),
        _ => false
    };

    /// <summary>
    /// Upserts the user's settings. A blank <paramref name="apiKeyPlain"/> preserves the
    /// existing stored key (so users don't have to re-enter it to change the model), while
    /// switching away from a key-based provider clears it.
    /// </summary>
    public async Task SaveAsync(string userId, AiProvider provider, string? endpoint, string? model, string? apiKeyPlain)
    {
        var settings = await GetAsync();
        if (settings is null)
        {
            settings = new AiSettings { UserId = userId };
            _db.AiSettings.Add(settings);
        }

        settings.Provider = provider;
        settings.Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        settings.Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        if (!string.IsNullOrWhiteSpace(apiKeyPlain))
        {
            settings.ApiKeyProtected = _protector.Protect(apiKeyPlain.Trim());
        }
        else if (provider is AiProvider.None or AiProvider.ClaudeCli)
        {
            // Neither provider uses a key — don't keep a stale secret around.
            settings.ApiKeyProtected = null;
        }
        // else: preserve the existing encrypted key.

        await _db.SaveChangesAsync();
    }

    /// <summary>Decrypts settings into a runtime config. Returns null if the key can't be unprotected.</summary>
    public AiProviderConfig BuildConfig(AiSettings settings)
    {
        string? apiKey = null;
        if (!string.IsNullOrEmpty(settings.ApiKeyProtected))
        {
            try { apiKey = _protector.Unprotect(settings.ApiKeyProtected); }
            catch { apiKey = null; }
        }
        return new AiProviderConfig(settings.Endpoint, settings.Model, apiKey);
    }

    /// <summary>Builds the configured categorizer for the current user, or null when unavailable.</summary>
    public async Task<ISmartCategorizer?> CreateCategorizerAsync()
    {
        var settings = await GetAsync();
        if (settings is null || !IsUsable(settings)) return null;
        return Create(settings);
    }

    /// <summary>Builds a categorizer directly from settings (used by Test Connection before saving too).</summary>
    public ISmartCategorizer? Create(AiSettings settings)
    {
        switch (settings.Provider)
        {
            case AiProvider.ClaudeCli:
                return _cliStatus.Available ? new ClaudeCliProvider() : null;
            case AiProvider.AnthropicApi:
                return new AnthropicApiProvider(_httpClientFactory.CreateClient("ai"), BuildConfig(settings));
            case AiProvider.OpenAiCompatible:
                return new OpenAiCompatibleProvider(_httpClientFactory.CreateClient("ai"), BuildConfig(settings));
            default:
                return null;
        }
    }
}
