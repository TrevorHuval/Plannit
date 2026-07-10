using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class AccountsController : Controller
{
    private readonly AccountService _accountService;

    public AccountsController(AccountService accountService)
    {
        _accountService = accountService;
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
        await _accountService.CreateAsync(userId, model.Name, model.Type, model.Institution);
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
            Institution = account.Institution
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, AccountFormViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var success = await _accountService.UpdateAsync(id, model.Name, model.Type, model.Institution);
        if (!success) return NotFound();

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
    public async Task<IActionResult> AddSnapshot(AddSnapshotViewModel model)
    {
        if (!ModelState.IsValid) return RedirectToAction(nameof(Details), new { id = model.AccountId });

        await _accountService.AddSnapshotAsync(model.AccountId, model.Date, model.Balance);
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
        await _accountService.DeactivateAsync(id);
        return RedirectToAction(nameof(Index));
    }
}
