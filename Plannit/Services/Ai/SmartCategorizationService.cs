using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using MatchType = Plannit.Models.Entities.MatchType;

namespace Plannit.Services.Ai;

/// <summary>
/// Orchestrates Smart Categorization: gathers uncategorized transactions, groups them by
/// merchant, and applies accepted proposals (creating categories and optional rules). The
/// provider call itself lives in <see cref="ISmartCategorizer"/>; this service is the
/// app-side glue and is provider-agnostic.
/// </summary>
public class SmartCategorizationService
{
    private readonly ApplicationDbContext _db;
    private readonly CategorizationService _categorization;

    public SmartCategorizationService(ApplicationDbContext db, CategorizationService categorization)
    {
        _db = db;
        _categorization = categorization;
    }

    /// <summary>One uncategorized merchant cluster, with the transactions it covers.</summary>
    public record MerchantGroupDetail(
        string MerchantKey,
        string SampleDescription,
        IReadOnlyList<string> SampleDescriptions,
        int Count,
        decimal TotalAmount,
        decimal AvgAmount,
        int Sign,
        List<int> TransactionIds);

    /// <summary>A user-accepted proposal to apply on submit.</summary>
    public record AcceptedMapping(string MerchantKey, string CategoryName, bool IsNew, bool CreateRule);

    public record ApplyResult(int CategoriesCreated, int TransactionsUpdated, int RulesCreated);

    public async Task<int> CountUncategorizedAsync(int? accountId = null)
    {
        var query = _db.Transactions.Where(t => t.CategoryId == null && t.Amount != 0);
        if (accountId.HasValue) query = query.Where(t => t.AccountId == accountId.Value);
        return await query.CountAsync();
    }

    /// <summary>
    /// Groups uncategorized transactions by normalized merchant. Groups are ordered by
    /// transaction count so the highest-impact merchants land within the per-call cap.
    /// </summary>
    public async Task<List<MerchantGroupDetail>> GatherAsync(int? accountId = null, int maxGroups = SmartCategorizationPrompt.MaxGroupsPerCall)
    {
        var query = _db.Transactions.Where(t => t.CategoryId == null && t.Amount != 0);
        if (accountId.HasValue) query = query.Where(t => t.AccountId == accountId.Value);

        var transactions = await query
            .Select(t => new { t.Id, t.Description, t.Amount })
            .ToListAsync();

        var groups = transactions
            .GroupBy(t => RecurringDetectionService.NormalizeMerchant(t.Description))
            .Where(g => g.Key.Length > 0)
            .Select(g =>
            {
                var total = g.Sum(t => t.Amount);
                var avg = Math.Round(total / g.Count(), 2);
                var samples = g.Select(t => t.Description)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d.Length)
                    .Take(3)
                    .ToList();
                return new MerchantGroupDetail(
                    MerchantKey: g.Key,
                    SampleDescription: samples.FirstOrDefault() ?? g.Key,
                    SampleDescriptions: samples,
                    Count: g.Count(),
                    TotalAmount: total,
                    AvgAmount: avg,
                    Sign: avg < 0 ? -1 : 1,
                    TransactionIds: g.Select(t => t.Id).ToList());
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => Math.Abs(g.TotalAmount))
            .Take(maxGroups)
            .ToList();

        return groups;
    }

    public async Task<IReadOnlyList<string>> GetExistingCategoryNamesAsync()
    {
        return await _db.Categories.OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();
    }

    public SmartCategorizationRequest BuildRequest(IReadOnlyList<MerchantGroupDetail> groups, IReadOnlyList<string> existingCategories)
    {
        var inputs = groups
            .Select(g => new MerchantGroup(g.MerchantKey, g.SampleDescriptions, g.Count, g.AvgAmount, g.Sign))
            .ToList();
        return new SmartCategorizationRequest(inputs, existingCategories, SmartCategorizationPrompt.MaxCategories);
    }

    /// <summary>
    /// Applies accepted mappings. Re-derives which transactions are still uncategorized from the
    /// merchant key (never trusts client-posted transaction IDs), creates any accepted new
    /// categories within the 25-cap, sets the category on matching transactions, and optionally
    /// writes a <see cref="CategoryRule"/> so future imports categorize without an AI call.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(string userId, IReadOnlyList<AcceptedMapping> accepted, int? accountId = null)
    {
        if (accepted.Count == 0) return new ApplyResult(0, 0, 0);

        // Current category name → id, and live count for cap enforcement.
        var categories = await _db.Categories.ToListAsync();
        var byName = categories.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var categoryCount = categories.Count;

        // Re-gather current uncategorized groups so we only touch still-uncategorized rows.
        var groups = await GatherAsync(accountId, int.MaxValue);
        var byMerchant = groups.ToDictionary(g => g.MerchantKey, g => g.TransactionIds, StringComparer.OrdinalIgnoreCase);

        int categoriesCreated = 0, transactionsUpdated = 0, rulesCreated = 0;
        var seenMerchants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in accepted)
        {
            if (string.IsNullOrWhiteSpace(mapping.CategoryName)) continue;
            if (!seenMerchants.Add(mapping.MerchantKey)) continue;
            if (!byMerchant.TryGetValue(mapping.MerchantKey, out var txnIds) || txnIds.Count == 0) continue;

            // Resolve or create the category.
            if (!byName.TryGetValue(mapping.CategoryName, out var categoryId))
            {
                if (categoryCount >= SmartCategorizationPrompt.MaxCategories) continue; // cap reached
                var created = await _categorization.CreateCategoryAsync(userId, mapping.CategoryName.Trim(), null);
                categoryId = created.Id;
                byName[created.Name] = created.Id;
                categoryCount++;
                categoriesCreated++;
            }

            var updated = await _db.Transactions
                .Where(t => txnIds.Contains(t.Id) && t.CategoryId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, categoryId));
            transactionsUpdated += updated;

            if (mapping.CreateRule)
            {
                var exists = await _db.CategoryRules.AnyAsync(r =>
                    r.CategoryId == categoryId && r.MatchText == mapping.MerchantKey && r.MatchType == MatchType.Contains);
                if (!exists)
                {
                    await _categorization.CreateRuleAsync(userId, mapping.MerchantKey, MatchType.Contains, categoryId, 50);
                    rulesCreated++;
                }
            }
        }

        return new ApplyResult(categoriesCreated, transactionsUpdated, rulesCreated);
    }
}
