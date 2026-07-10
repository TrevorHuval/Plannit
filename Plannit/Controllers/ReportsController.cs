using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class ReportsController : Controller
{
    private readonly ReportsService _reportsService;

    public ReportsController(ReportsService reportsService)
    {
        _reportsService = reportsService;
    }

    public async Task<IActionResult> Index(int? year, int? month)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;
        var m = month ?? today.Month;

        var vm = new ReportsViewModel
        {
            Year = y,
            Month = m,
            SpendByCategory = await _reportsService.GetSpendByCategoryAsync(y, m),
            MonthlyHistory = await _reportsService.GetMonthlySpendHistoryAsync(),
            IncomeExpense = await _reportsService.GetIncomeExpenseSummaryAsync(y, m),
            TopMerchants = await _reportsService.GetTopMerchantsAsync(y, m)
        };

        return View(vm);
    }
}
