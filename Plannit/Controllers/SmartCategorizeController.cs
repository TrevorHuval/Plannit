using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services.Ai;

namespace Plannit.Controllers;

[Authorize]
public class SmartCategorizeController : Controller
{
    // Proposals at or above this confidence are pre-checked on the review screen.
    private const double PreCheckThreshold = 0.8;

    private readonly SmartCategorizationService _smart;
    private readonly AiSettingsService _aiSettings;

    public SmartCategorizeController(SmartCategorizationService smart, AiSettingsService aiSettings)
    {
        _smart = smart;
        _aiSettings = aiSettings;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int? accountId)
    {
        var categorizer = await _aiSettings.CreateCategorizerAsync();
        if (categorizer is null)
        {
            TempData["Error"] = "No AI provider is configured. Set one up in Settings first.";
            return RedirectToAction("Ai", "Settings");
        }

        var uncategorizedCount = await _smart.CountUncategorizedAsync(accountId);
        var groups = await _smart.GatherAsync(accountId);
        if (groups.Count == 0)
        {
            TempData["Message"] = "Nothing to categorize — no uncategorized transactions found.";
            return RedirectToAction("Index", "Transactions");
        }

        var existing = await _smart.GetExistingCategoryNamesAsync();
        var request = _smart.BuildRequest(groups, existing);
        var result = await categorizer.CategorizeAsync(request);

        var vm = new SmartCategorizeReviewViewModel
        {
            AccountId = accountId,
            ProviderName = categorizer.Name,
            UncategorizedCount = uncategorizedCount,
            GroupCount = groups.Count
        };

        if (!result.Success)
        {
            vm.ProviderError = result.ErrorMessage;
            return View(vm);
        }

        var proposalByMerchant = result.Proposals
            .GroupBy(p => p.MerchantKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            proposalByMerchant.TryGetValue(g.MerchantKey, out var proposal);
            var hasProposal = proposal?.CategoryName is not null;
            vm.Rows.Add(new SmartCategorizeRowViewModel
            {
                MerchantKey = g.MerchantKey,
                SampleDescription = g.SampleDescription,
                Count = g.Count,
                TotalAmount = g.TotalAmount,
                Sign = g.Sign,
                ProposedCategory = proposal?.CategoryName,
                IsNew = proposal?.IsNewCategorySuggestion ?? false,
                Confidence = proposal?.Confidence ?? 0,
                Accepted = hasProposal && (proposal!.Confidence >= PreCheckThreshold),
                CreateRule = true
            });
        }

        // Most confident and actionable first; untouched merchants sink to the bottom.
        vm.Rows = vm.Rows
            .OrderByDescending(r => r.HasProposal)
            .ThenByDescending(r => r.Confidence)
            .ToList();

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(SmartCategorizeApplyViewModel model)
    {
        var accepted = model.Rows
            .Where(r => r.Accepted && !string.IsNullOrWhiteSpace(r.CategoryName))
            .Select(r => new SmartCategorizationService.AcceptedMapping(
                r.MerchantKey, r.CategoryName!.Trim(), r.IsNew, r.CreateRule))
            .ToList();

        if (accepted.Count == 0)
        {
            TempData["Error"] = "No proposals were selected.";
            return RedirectToAction("Index", "Transactions");
        }

        var result = await _smart.ApplyAsync(UserId, accepted, model.AccountId);

        var parts = new List<string> { $"categorized {result.TransactionsUpdated} transaction{Plural(result.TransactionsUpdated)}" };
        if (result.CategoriesCreated > 0) parts.Add($"created {result.CategoriesCreated} new categor{(result.CategoriesCreated == 1 ? "y" : "ies")}");
        if (result.RulesCreated > 0) parts.Add($"added {result.RulesCreated} rule{Plural(result.RulesCreated)}");

        TempData["Message"] = "Smart Categorize: " + string.Join(", ", parts) + ".";
        return RedirectToAction("Index", "Transactions");
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
