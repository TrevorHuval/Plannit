using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Plannit.Services.Ai;

/// <summary>
/// Calls the Anthropic Messages API directly with a user-supplied API key. Recommended model
/// is <c>claude-haiku-4-5</c> — categorization is a cheap classification task.
/// </summary>
public class AnthropicApiProvider : PromptBasedCategorizer
{
    public const string DefaultModel = "claude-haiku-4-5";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicApiProvider(HttpClient http, AiProviderConfig config)
    {
        _http = http;
        _apiKey = config.ApiKey ?? throw new InvalidOperationException("Anthropic API key is not configured.");
        _model = string.IsNullOrWhiteSpace(config.Model) ? DefaultModel : config.Model.Trim();
    }

    public override string Name => "Anthropic API";

    protected override async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            max_tokens = 4096,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Truncate(content)}");

        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("content", out var contentArr) &&
            contentArr.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in contentArr.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
            return sb.ToString();
        }
        return content;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
