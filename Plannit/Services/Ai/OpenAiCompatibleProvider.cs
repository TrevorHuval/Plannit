using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Plannit.Services.Ai;

/// <summary>
/// Calls any OpenAI-format chat-completions endpoint. Covers OpenAI itself, local runners
/// (Ollama, LM Studio), and other compatible gateways — configured by base URL, model, and an
/// optional key. Maximizes openness for users who aren't on Claude.
/// </summary>
public class OpenAiCompatibleProvider : PromptBasedCategorizer
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string _model;

    public OpenAiCompatibleProvider(HttpClient http, AiProviderConfig config)
    {
        _http = http;
        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException("Endpoint (base URL) is required for an OpenAI-compatible provider.");
        _endpoint = BuildCompletionsUrl(config.Endpoint.Trim());
        _apiKey = string.IsNullOrWhiteSpace(config.ApiKey) ? null : config.ApiKey.Trim();
        _model = string.IsNullOrWhiteSpace(config.Model)
            ? throw new InvalidOperationException("A model name is required for an OpenAI-compatible provider.")
            : config.Model.Trim();
    }

    public override string Name => "OpenAI-compatible";

    // Accepts either a bare base URL (…/v1) or a full …/chat/completions URL.
    private static string BuildCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return trimmed + "/chat/completions";
    }

    protected override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (_apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Truncate(content)}");

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var msgContent))
            {
                return msgContent.GetString() ?? string.Empty;
            }
        }
        return content;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
