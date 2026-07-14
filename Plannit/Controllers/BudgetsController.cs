using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class BudgetsController : Controller
{
    private readonly BudgetService _budgetService;
    private readonly CategorizationService _categorizationService;
    private readonly RecurringDetectionService _recurringService;
    private readonly BillService _billService;

    public BudgetsController(
        BudgetService budgetService,
        CategorizationService categorizationService,
        RecurringDetectionService recurringService,
        BillService billService)
    {
        _budgetService = budgetService;
        _categorizationService = categorizationService;
        _recurringService = recurringService;
        _billService = billService;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var selectedMonth = new DateOnly(year ?? today.Year, month ?? today.Month, 1);

        var statuses = await _budgetService.GetBudgetStatusAsync(selectedMonth);

        var vm = new BudgetIndexViewModel
        {
            Month = selectedMonth,
            Statuses = statuses
        };

        return View(vm);
    }

    public async Task<IActionResult> Settings()
    {
        var categories = await _categorizationService.GetAllCategoriesAsync();
        var budgets = await _budgetService.GetAllBudgetsAsync();

        var rows = categories
            .Where(c => c.ParentId == null)
            .Select(c =>
            {
                var budget = budgets.FirstOrDefault(b => b.CategoryId == c.Id);
                return new BudgetCategoryRow
                {
                    CategoryId = c.Id,
                    CategoryName = c.Name,
                    MonthlyAmount = budget?.MonthlyAmount,
                    BudgetId = budget?.Id
                };
            })
            .OrderBy(r => r.CategoryName)
            .ToList();

        return View(new BudgetSettingsViewModel { Categories = rows });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBudgets(Dictionary<int, decimal?> amounts)
    {
        var existingBudgets = await _budgetService.GetAllBudgetsAsync();

        foreach (var (categoryId, amount) in amounts)
        {
            if (amount.HasValue && amount.Value > 0)
            {
                await _budgetService.CreateOrUpdateBudgetAsync(UserId, categoryId, amount.Value);
            }
            else
            {
                var existing = existingBudgets.FirstOrDefault(b => b.CategoryId == categoryId);
                if (existing is not null)
                    await _budgetService.DeleteBudgetAsync(existing.Id);
            }
        }

        TempData["Success"] = "Budgets saved successfully.";
        return RedirectToAction(nameof(Settings));
    }

    public async Task<IActionResult> Recurring()
    {
        var groups = await _recurringService.DetectRecurringAsync();
        var bills = await _billService.GetAllAsync();
        var promotedKeys = bills
            .Where(b => b.IsActive)
            .Select(b => (b.MerchantKey, b.IsIncome))
            .ToHashSet();
        var vm = new RecurringIndexViewModel { RecurringGroups = groups, PromotedKeys = promotedKeys };
        return View(vm);
    }
}
