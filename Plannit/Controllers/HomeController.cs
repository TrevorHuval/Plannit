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
    private readonly RecurringDetectionService _recurringService;

    public HomeController(
        NetWorthService netWorthService,
        BudgetService budgetService,
        RecurringDetectionService recurringService)
    {
        _netWorthService = netWorthService;
        _budgetService = budgetService;
        _recurringService = recurringService;
    }

    public async Task<IActionResult> Index()
    {
        var netWorth = await _netWorthService.GetCurrentNetWorthAsync();
        var typeTotals = await _netWorthService.GetTotalsByTypeAsync();
        var history = await _netWorthService.GetNetWorthHistoryAsync();
        var budgetAlerts = await _budgetService.GetTopBudgetAlertsAsync(3);
        var upcomingRecurring = await _recurringService.GetUpcomingAsync(7);

        var vm = new DashboardViewModel
        {
            NetWorth = netWorth,
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
            UpcomingRecurring = upcomingRecurring
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
