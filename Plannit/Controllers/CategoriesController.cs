using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class CategoriesController : Controller
{
    private readonly CategorizationService _categorizationService;
    private readonly TransactionService _transactionService;
    private readonly DataManagementService _dataService;
    private readonly ApplicationDbContext _db;

    public CategoriesController(
        CategorizationService categorizationService,
        TransactionService transactionService,
        DataManagementService dataService,
        ApplicationDbContext db)
    {
        _categorizationService = categorizationService;
        _transactionService = transactionService;
        _dataService = dataService;
        _db = db;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index()
    {
        var categories = await _categorizationService.GetAllCategoriesAsync();
        var txnCounts = await _db.Transactions
            .Where(t => t.CategoryId != null)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        var ruleCounts = await _db.CategoryRules
            .GroupBy(r => r.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);

        var vm = new CategoryListViewModel
        {
            Categories = categories.Select(c => new CategoryItemViewModel
            {
                Id = c.Id,
                Name = c.Name,
                ParentName = c.Parent?.Name,
                IsSystem = c.IsSystem,
                RuleCount = ruleCounts.GetValueOrDefault(c.Id),
                TransactionCount = txnCounts.GetValueOrDefault(c.Id)
            }).ToList()
        };

        return View(vm);
    }

    public async Task<IActionResult> Create()
    {
        var categories = await _categorizationService.GetAllCategoriesAsync();
        return View(new CategoryFormViewModel
        {
            AvailableParents = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.AvailableParents = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        await _categorizationService.CreateCategoryAsync(UserId, model.Name, model.ParentId);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var category = await _categorizationService.GetCategoryByIdAsync(id);
        if (category is null) return NotFound();

        var categories = await _categorizationService.GetAllCategoriesAsync();
        return View(new CategoryFormViewModel
        {
            Id = category.Id,
            Name = category.Name,
            ParentId = category.ParentId,
            AvailableParents = categories
                .Where(c => c.Id != id)
                .Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CategoryFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.AvailableParents = categories
                .Where(c => c.Id != id)
                .Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        var success = await _categorizationService.UpdateCategoryAsync(id, model.Name, model.ParentId);
        if (!success) return NotFound();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await _categorizationService.DeleteCategoryAsync(id);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Rules()
    {
        var rules = await _categorizationService.GetRulesAsync();
        var vm = new CategoryRuleListViewModel
        {
            Rules = rules.Select(r => new CategoryRuleItemViewModel
            {
                Id = r.Id,
                MatchText = r.MatchText,
                MatchType = r.MatchType,
                CategoryName = r.Category.Name,
                Priority = r.Priority
            }).ToList()
        };
        return View(vm);
    }

    public async Task<IActionResult> CreateRule()
    {
        var categories = await _categorizationService.GetAllCategoriesAsync();
        return View(new CategoryRuleFormViewModel
        {
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRule(CategoryRuleFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        await _categorizationService.CreateRuleAsync(UserId, model.MatchText, model.MatchType, model.CategoryId, model.Priority);
        return RedirectToAction(nameof(Rules));
    }

    public async Task<IActionResult> EditRule(int id)
    {
        var rules = await _categorizationService.GetRulesAsync();
        var rule = rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return NotFound();

        var categories = await _categorizationService.GetAllCategoriesAsync();
        return View(new CategoryRuleFormViewModel
        {
            Id = rule.Id,
            MatchText = rule.MatchText,
            MatchType = rule.MatchType,
            CategoryId = rule.CategoryId,
            Priority = rule.Priority,
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRule(int id, CategoryRuleFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        var success = await _categorizationService.UpdateRuleAsync(id, model.MatchText, model.MatchType, model.CategoryId, model.Priority);
        if (!success) return NotFound();

        return RedirectToAction(nameof(Rules));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRule(int id)
    {
        await _categorizationService.DeleteRuleAsync(id);
        return RedirectToAction(nameof(Rules));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTransactionCategory(int transactionId, int? categoryId, string? returnUrl)
    {
        await _categorizationService.CategorizeTransactionAsync(transactionId, categoryId);
        if (!string.IsNullOrEmpty(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction("Index", "Transactions");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecategorizeUncategorized()
    {
        var count = await _categorizationService.ApplyRulesToUncategorizedAsync();
        TempData["Message"] = $"Categorized {count} transaction{(count != 1 ? "s" : "")}.";
        return RedirectToAction("Index", "Transactions");
    }

    public async Task<IActionResult> CreateRuleFromTransaction(int transactionId)
    {
        var txn = await _transactionService.GetByIdAsync(transactionId);
        if (txn is null) return NotFound();

        var categories = await _categorizationService.GetAllCategoriesAsync();
        return View(new CreateRuleFromTransactionViewModel
        {
            TransactionId = txn.Id,
            Description = txn.Description,
            MatchText = txn.Description,
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRuleFromTransaction(CreateRuleFromTransactionViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        var rule = await _categorizationService.CreateRuleAsync(UserId, model.MatchText, model.MatchType, model.CategoryId, model.Priority);

        await _categorizationService.CategorizeTransactionAsync(model.TransactionId, model.CategoryId);

        TempData["Message"] = $"Rule created and transaction categorized as '{rule.Category?.Name ?? "selected category"}'.";
        return RedirectToAction("Index", "Transactions");
    }

    public async Task<IActionResult> Merge(int id)
    {
        var source = await _categorizationService.GetCategoryByIdAsync(id);
        if (source is null) return NotFound();

        var categories = await _categorizationService.GetAllCategoriesAsync();
        var vm = new MergeCategoryViewModel
        {
            SourceId = id,
            SourceName = source.Name,
            AvailableTargets = categories
                .Where(c => c.Id != id)
                .Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Merge(MergeCategoryViewModel model)
    {
        if (!ModelState.IsValid)
        {
            var categories = await _categorizationService.GetAllCategoriesAsync();
            model.AvailableTargets = categories
                .Where(c => c.Id != model.SourceId)
                .Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList();
            return View(model);
        }

        var (success, message) = await _dataService.MergeCategoriesAsync(model.SourceId, model.TargetId);
        TempData[success ? "Message" : "Error"] = message;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> TestRule(string? matchText, string? matchType)
    {
        var categories = await _categorizationService.GetAllCategoriesAsync();
        var vm = new RuleTestViewModel
        {
            MatchText = matchText,
            MatchType = matchType ?? "Contains",
            Categories = categories.Select(c => new CategoryOption { Id = c.Id, Name = c.Name }).ToList()
        };

        if (!string.IsNullOrWhiteSpace(matchText))
        {
            var mt = Enum.TryParse<Models.Entities.MatchType>(vm.MatchType, out var parsed)
                ? parsed : Models.Entities.MatchType.Contains;
            var matches = await _dataService.TestRuleMatchesAsync(matchText, mt);
            vm.MatchingTransactions = matches.Select(t => new TransactionMatchViewModel
            {
                Id = t.Id,
                Date = t.Date,
                Description = t.Description,
                Amount = t.Amount,
                AccountName = t.Account.Name,
                CurrentCategory = t.Category?.Name
            }).ToList();
        }

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveRulePriority(int id, bool moveUp)
    {
        await _dataService.MoveRulePriorityAsync(id, moveUp);
        return RedirectToAction(nameof(Rules));
    }
}
