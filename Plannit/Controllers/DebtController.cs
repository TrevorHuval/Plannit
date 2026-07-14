using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class DebtController : Controller
{
    private readonly LoanService _loanService;

    public DebtController(LoanService loanService)
    {
        _loanService = loanService;
    }

    public async Task<IActionResult> Index()
    {
        var accounts = await _loanService.GetDebtAccountsAsync();
        var vm = new DebtIndexViewModel { Accounts = accounts.Select(ToSummary).ToList() };
        return View(vm);
    }

    public async Task<IActionResult> Amortization(int id)
    {
        var account = await _loanService.GetByIdAsync(id);
        if (account is null) return NotFound();

        if (account.InterestRate is null || account.MinimumPayment is null)
        {
            TempData["Error"] = "Add an interest rate and minimum payment to this account to see its amortization schedule.";
            return RedirectToAction(nameof(Index));
        }

        var principal = account.OriginalPrincipal ?? LatestBalance(account);
        var schedule = LoanService.RunAmortizationSchedule(principal, account.InterestRate.Value, account.MinimumPayment.Value);

        var vm = new AmortizationViewModel
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Principal = principal,
            AnnualRate = account.InterestRate.Value,
            MonthlyPayment = account.MinimumPayment.Value,
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            Schedule = schedule
        };
        return View(vm);
    }

    public async Task<IActionResult> Compare(decimal? extraPayment)
    {
        var accounts = await _loanService.GetDebtAccountsAsync();
        var summaries = accounts.Select(ToSummary).ToList();
        var extra = extraPayment ?? 0m;

        var vm = new DebtCompareViewModel
        {
            IncludedAccounts = summaries.Where(a => a.HasPayoffData).ToList(),
            ExcludedAccounts = summaries.Where(a => !a.HasPayoffData).ToList(),
            ExtraPayment = extra
        };

        if (vm.IncludedAccounts.Count > 0)
        {
            var inputs = vm.IncludedAccounts.Select(a => new DebtAccountInput
            {
                AccountId = a.Id,
                Name = a.Name,
                Balance = a.Balance,
                AnnualRate = a.InterestRate!.Value,
                MinimumPayment = a.MinimumPayment!.Value
            }).ToList();

            vm.Comparison = LoanService.CompareStrategies(inputs, extra, DateOnly.FromDateTime(DateTime.Today));
        }

        return View(vm);
    }

    private static DebtAccountSummary ToSummary(Account a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        TypeDisplayName = NetWorthService.FormatAccountType(a.Type),
        Balance = LatestBalance(a),
        InterestRate = a.InterestRate,
        MinimumPayment = a.MinimumPayment
    };

    private static decimal LatestBalance(Account a) =>
        a.Snapshots.OrderByDescending(s => s.Date).Select(s => s.Balance).FirstOrDefault();
}
