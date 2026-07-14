using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class GoalsController : Controller
{
    private readonly SavingsGoalService _goalService;

    public GoalsController(SavingsGoalService goalService)
    {
        _goalService = goalService;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index()
    {
        var goals = await _goalService.GetAllAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var vm = goals.Select(g =>
        {
            var progress = SavingsGoalService.ComputeProgress(g, today);
            return new SavingsGoalCardViewModel
            {
                Id = g.Id,
                Name = g.Name,
                TargetAmount = g.TargetAmount,
                TargetDate = g.TargetDate,
                LinkedAccountName = g.LinkedAccount?.Name,
                CurrentAmount = progress.CurrentAmount,
                Percentage = progress.Percentage,
                IsComplete = progress.IsComplete,
                AmountRemaining = progress.AmountRemaining,
                ProjectedCompletionDate = progress.ProjectedCompletionDate
            };
        }).ToList();

        return View(vm);
    }

    public async Task<IActionResult> Create()
    {
        var vm = new SavingsGoalFormViewModel { LinkableAccounts = await GetAccountOptionsAsync() };
        return View("Edit", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var goal = await _goalService.GetByIdAsync(id);
        if (goal is null) return NotFound();

        var vm = new SavingsGoalFormViewModel
        {
            Id = goal.Id,
            Name = goal.Name,
            TargetAmount = goal.TargetAmount,
            TargetDate = goal.TargetDate,
            LinkedAccountId = goal.LinkedAccountId,
            ManualProgress = goal.ManualProgress,
            LinkableAccounts = await GetAccountOptionsAsync()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SavingsGoalFormViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.LinkableAccounts = await GetAccountOptionsAsync();
            return View("Edit", model);
        }

        if (model.Id.HasValue)
            await _goalService.UpdateAsync(model.Id.Value, model.Name, model.TargetAmount, model.TargetDate, model.LinkedAccountId, model.ManualProgress);
        else
            await _goalService.CreateAsync(UserId, model.Name, model.TargetAmount, model.TargetDate, model.LinkedAccountId, model.ManualProgress);

        TempData["Success"] = "Goal saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await _goalService.DeleteAsync(id);
        TempData["Success"] = "Goal deleted.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<List<AccountOption>> GetAccountOptionsAsync()
    {
        var accounts = await _goalService.GetLinkableAccountsAsync();
        return accounts.Select(a => new AccountOption { Id = a.Id, Name = a.Name }).ToList();
    }
}
