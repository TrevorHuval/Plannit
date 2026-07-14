using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class AccountsController : Controller
{
    private readonly AccountService _accountService;
    private readonly AuditService _audit;

    public AccountsController(AccountService accountService, AuditService audit)
    {
        _accountService = accountService;
        _audit = audit;
    }

    public async Task<IActionResult> Index()
    {
        var accounts = await _accountService.GetAllAsync();

        var groups = accounts
            .GroupBy(a => a.Type)
            .OrderBy(g => g.Key)
            .Select(g => new AccountGroupViewModel
            {
                Type = g.Key,
                TypeDisplayName = NetWorthService.FormatAccountType(g.Key),
                Accounts = g.Select(a =>
                {
                    var latest = a.Snapshots.MaxBy(s => s.Date);
                    return new AccountSummaryViewModel
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Institution = a.Institution,
                        Type = a.Type,
                        LatestBalance = latest?.Balance,
                        LatestSnapshotDate = latest?.Date
                    };
                }).ToList()
            }).ToList();

        return View(new AccountListViewModel { Groups = groups });
    }

    public IActionResult Create()
    {
        return View(new AccountFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AccountFormViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await _accountService.CreateAsync(userId, model.Name, model.Type, model.Institution,
            model.InterestRatePercent.HasValue ? model.InterestRatePercent / 100m : null,
            model.MinimumPayment, model.OriginalPrincipal);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var account = await _accountService.GetByIdAsync(id);
        if (account is null) return NotFound();

        return View(new AccountFormViewModel
        {
            Id = account.Id,
            Name = account.Name,
            Type = account.Type,
            Institution = account.Institution,
            InterestRatePercent = account.InterestRate.HasValue ? account.InterestRate * 100m : null,
            MinimumPayment = account.MinimumPayment,
            OriginalPrincipal = account.OriginalPrincipal,
            RowVersion = account.RowVersion
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AccountFormViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        try
        {
            var success = await _accountService.UpdateAsync(id, model.Name, model.Type, model.Institution, model.RowVersion,
                model.InterestRatePercent.HasValue ? model.InterestRatePercent / 100m : null,
                model.MinimumPayment, model.OriginalPrincipal);
            if (!success) return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError(string.Empty,
                "This account was changed by another update since you opened it. Review your values and save again to overwrite.");

            var current = await _accountService.GetByIdAsync(id);
            if (current is null) return NotFound();
            model.RowVersion = current.RowVersion;
            return View(model);
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var account = await _accountService.GetByIdAsync(id);
        if (account is null) return NotFound();

        var vm = new AccountDetailViewModel
        {
            Id = account.Id,
            Name = account.Name,
            Type = account.Type,
            TypeDisplayName = NetWorthService.FormatAccountType(account.Type),
            Institution = account.Institution,
            IsActive = account.IsActive,
            Snapshots = account.Snapshots
                .OrderByDescending(s => s.Date)
                .Select(s => new SnapshotViewModel
                {
                    Id = s.Id,
                    Date = s.Date,
                    Balance = s.Balance
                }).ToList()
        };

        ViewBag.AddSnapshot = new AddSnapshotViewModel { AccountId = id };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSnapshot(AddSnapshotViewModel model, string? returnUrl)
    {
        if (!ModelState.IsValid)
        {
            if (!string.IsNullOrEmpty(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToAction(nameof(Details), new { id = model.AccountId });
        }

        await _accountService.AddSnapshotAsync(model.AccountId, model.Date, model.Balance);

        if (!string.IsNullOrEmpty(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Details), new { id = model.AccountId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSnapshot(int id, int accountId)
    {
        await _accountService.DeleteSnapshotAsync(id);
        return RedirectToAction(nameof(Details), new { id = accountId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var success = await _accountService.DeactivateAsync(id);
        if (success)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await _audit.LogAsync(userId, "AccountDeleted", $"Account #{id} deactivated", HttpContext.Connection.RemoteIpAddress?.ToString());
        }
        return RedirectToAction(nameof(Index));
    }
}
