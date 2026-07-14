using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly NetWorthService _netWorthService;
    private readonly BudgetService _budgetService;
    private readonly BillService _billService;
    private readonly ReportsService _reportsService;
    private readonly TransactionService _transactionService;
    private readonly CategorizationService _categorizationService;

    public HomeController(
        NetWorthService netWorthService,
        BudgetService budgetService,
        BillService billService,
        ReportsService reportsService,
        TransactionService transactionService,
        CategorizationService categorizationService)
    {
        _netWorthService = netWorthService;
        _budgetService = budgetService;
        _billService = billService;
        _reportsService = reportsService;
        _transactionService = transactionService;
        _categorizationService = categorizationService;
    }

    public async Task<IActionResult> Index()
    {
        var netWorth = await _netWorthService.GetCurrentNetWorthAsync();
        var typeTotals = await _netWorthService.GetTotalsByTypeAsync();
        var history = await _netWorthService.GetNetWorthHistoryAsync();
        var budgetAlerts = await _budgetService.GetTopBudgetAlertsAsync(3);
        await _billService.ReconcileAsync();
        var upcomingBills = await _billService.GetUpcomingAsync(7);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var oneMonthAgo = today.AddMonths(-1);
        var twelveMonthsAgo = today.AddMonths(-12);

        var netWorth1M = await _netWorthService.GetNetWorthAtDateAsync(oneMonthAgo);
        var netWorth12M = await _netWorthService.GetNetWorthAtDateAsync(twelveMonthsAgo);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var incomeExpense = await _reportsService.GetIncomeExpenseSummaryAsync(monthStart, monthEnd);

        var topCategories = await _reportsService.GetSpendByCategoryAsync(monthStart, monthEnd);

        var budgetStatuses = await _budgetService.GetBudgetStatusAsync(today);
        var totalBudgeted = budgetStatuses.Sum(b => b.Budget.MonthlyAmount);

        var (recentTxns, _) = await _transactionService.GetFilteredAsync(
            null, null, null, null, null, 1, 10);

        var categories = await _categorizationService.GetAllCategoriesAsync();

        var staleAccounts = await _netWorthService.GetStaleAccountsAsync(30);

        var vm = new DashboardViewModel
        {
            NetWorth = netWorth,
            NetWorth1MonthAgo = netWorth1M,
            NetWorth12MonthsAgo = netWorth12M,
            HasAccounts = typeTotals.Count > 0,
            TypeTotals = typeTotals
                .OrderBy(kv => kv.Key)
                .Select(kv => new TypeTotalViewModel
                {
                    Type = kv.Key,
                    TypeDisplayName = NetWorthService.FormatAccountType(kv.Key),
                    Total = kv.Value,
                    IsLiability = NetWorthService.IsLiability(kv.Key)
                }).ToList(),
            NetWorthHistory = history.Select(h => new NetWorthPointViewModel
            {
                Date = h.Date.ToString("yyyy-MM-dd"),
                NetWorth = h.NetWorth
            }).ToList(),
            BudgetAlerts = budgetAlerts,
            UpcomingBills = upcomingBills,
            ThisMonthIncome = incomeExpense.Income,
            ThisMonthSpending = incomeExpense.Expenses,
            ThisMonthBudgetTotal = totalBudgeted,
            TopCategories = topCategories.Categories.Take(5).ToList(),
            RecentTransactions = recentTxns.Select(t => new RecentTransactionViewModel
            {
                Id = t.Id,
                Date = t.Date,
                Description = t.Description,
                Amount = t.Amount,
                CategoryId = t.CategoryId,
                CategoryName = t.Category?.Name,
                AccountName = t.Account.Name
            }).ToList(),
            Categories = categories.Select(c => new CategoryOption
            {
                Id = c.Id,
                Name = c.Name
            }).ToList(),
            StaleAccounts = staleAccounts.Select(s => new StaleAccountViewModel
            {
                AccountId = s.AccountId,
                AccountName = s.AccountName,
                DaysSinceUpdate = s.DaysSinceUpdate
            }).ToList()
        };

        return View(vm);
    }

    [AllowAnonymous]
    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
