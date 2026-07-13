using System.Text;
using System.Text.Json;

namespace Plannit.Services.Ai;

/// <summary>
/// Builds the shared prompt used by every provider. Pure and deterministic so it can be
/// unit-tested and so provider behavior stays identical regardless of backend.
/// </summary>
public static class SmartCategorizationPrompt
{
    public const int MaxCategories = 25;
    public const int MaxGroupsPerCall = 50;

    private static readonly JsonSerializerOptions MerchantJsonOptions = new()
    {
        WriteIndented = false
    };

    public static (string System, string User) Build(SmartCategorizationRequest request)
    {
        var system = BuildSystem(request);
        var user = BuildUser(request);
        return (system, user);
    }

    private static string BuildSystem(SmartCategorizationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You categorize bank and credit-card transactions for a personal finance app.");
        sb.AppendLine("You are given merchant groups (each a cluster of transactions with the same merchant) and the user's existing category names.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Map each merchant to one of the EXISTING categories whenever a reasonable fit exists. Prefer existing categories.");
        sb.AppendLine("- A negative average amount means money out (spending); a positive average means money in (income/refund). Use the sign to disambiguate.");
        if (request.AtCategoryCap)
        {
            sb.AppendLine($"- The user is AT the category cap ({request.CategoryCount}/{request.MaxCategories}). Do NOT suggest any new categories; use existing ones or return null.");
        }
        else
        {
            sb.AppendLine($"- Suggest a NEW category (isNew=true) ONLY when a group has at least 5 transactions AND no existing category is a sensible fit. The user has {request.CategoryCount} of a maximum {request.MaxCategories} categories, so at most {request.MaxCategories - request.CategoryCount} new categories may be suggested in total.");
        }
        sb.AppendLine("- When you are unsure, set \"category\" to null and \"confidence\" to a low value. Leaving a merchant untouched is always acceptable and preferred over a wrong guess.");
        sb.AppendLine("- \"confidence\" is a number from 0.0 to 1.0 reflecting how certain the mapping is.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object, no prose, no markdown fences, matching exactly this schema:");
        sb.AppendLine("{\"suggestions\":[{\"merchantKey\":\"<the exact merchantKey given>\",\"category\":\"<existing or new category name, or null>\",\"confidence\":<0.0-1.0>,\"isNew\":<true|false>}]}");
        sb.AppendLine("Return one entry per merchant group. Echo each merchantKey exactly as provided.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildUser(SmartCategorizationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Existing categories:");
        sb.AppendLine(request.ExistingCategories.Count > 0
            ? string.Join(", ", request.ExistingCategories)
            : "(none yet)");
        sb.AppendLine();
        sb.AppendLine("Merchant groups (JSON):");

        var payload = request.Groups.Select(g => new
        {
            merchantKey = g.MerchantKey,
            samples = g.SampleDescriptions,
            count = g.Count,
            avgAmount = decimal.Round(g.AvgAmount, 2),
            sign = g.Sign < 0 ? "out" : "in"
        });
        sb.AppendLine(JsonSerializer.Serialize(payload, MerchantJsonOptions));
        return sb.ToString().TrimEnd();
    }
}
