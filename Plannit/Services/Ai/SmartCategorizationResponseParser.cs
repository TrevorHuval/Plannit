using System.Text.Json;

namespace Plannit.Services.Ai;

/// <summary>
/// Parses a provider's raw text response into <see cref="CategoryProposal"/>s. Deliberately
/// forgiving: malformed or partial JSON degrades to "no suggestions" rather than throwing,
/// so a bad model reply never surfaces as an error to the user. Also enforces the app's
/// invariants (echoed keys only, category cap, existing-vs-new resolution) rather than
/// trusting the model to have followed them.
/// </summary>
public static class SmartCategorizationResponseParser
{
    public static IReadOnlyList<CategoryProposal> Parse(string? raw, SmartCategorizationRequest request)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return Array.Empty<CategoryProposal>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Array.Empty<CategoryProposal>(); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("suggestions", out var suggestions) ||
                suggestions.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CategoryProposal>();
            }

            var validKeys = request.Groups.Select(g => g.MerchantKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingByName = request.ExistingCategories
                .ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            var remainingNewSlots = Math.Max(0, request.MaxCategories - request.CategoryCount);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var proposals = new List<CategoryProposal>();

            foreach (var item in suggestions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var merchantKey = GetString(item, "merchantKey");
                if (merchantKey is null || !validKeys.Contains(merchantKey)) continue;
                if (!seen.Add(merchantKey)) continue;

                var category = GetString(item, "category")?.Trim();
                if (string.IsNullOrWhiteSpace(category)) category = null;

                var confidence = Math.Clamp(GetDouble(item, "confidence"), 0.0, 1.0);

                bool isNew = false;
                if (category is not null)
                {
                    // Trust our own category list, not the model's isNew flag: if the name
                    // matches an existing category it is a mapping; otherwise it is new.
                    if (existingByName.TryGetValue(category, out var canonical))
                    {
                        category = canonical;
                        isNew = false;
                    }
                    else
                    {
                        isNew = true;
                    }
                }

                proposals.Add(new CategoryProposal(merchantKey, category, confidence, isNew));
            }

            return EnforceNewCategoryCap(proposals, remainingNewSlots);
        }
    }

    /// <summary>
    /// Keeps new-category suggestions within the remaining slots (highest confidence first);
    /// any excess new categories are downgraded to "no suggestion" so the 25-category cap holds.
    /// </summary>
    private static List<CategoryProposal> EnforceNewCategoryCap(List<CategoryProposal> proposals, int remainingNewSlots)
    {
        var distinctNew = proposals
            .Where(p => p.IsNewCategorySuggestion && p.CategoryName is not null)
            .GroupBy(p => p.CategoryName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, MaxConfidence = g.Max(p => p.Confidence) })
            .OrderByDescending(g => g.MaxConfidence)
            .ToList();

        if (distinctNew.Count <= remainingNewSlots) return proposals;

        var allowedNew = distinctNew.Take(remainingNewSlots)
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return proposals
            .Select(p => p.IsNewCategorySuggestion && p.CategoryName is not null && !allowedNew.Contains(p.CategoryName)
                ? p with { CategoryName = null, IsNewCategorySuggestion = false }
                : p)
            .ToList();
    }

    // Locate the outermost JSON object, tolerating markdown fences or leading/trailing prose.
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static double GetDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0.0;
        return el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) ? d : 0.0;
    }
}
