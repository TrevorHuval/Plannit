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

    public async Task<IActionResult> Index(DateOnly? date)
    {
        var selectedDate = date ?? DateOnly.FromDateTime(DateTime.Today);

        var vm = new ReportsViewModel
        {
            SelectedDate = selectedDate,
            SpendByCategory = await _reportsService.GetSpendByCategoryAsync(selectedDate, selectedDate),
            MonthlyHistory = await _reportsService.GetMonthlySpendHistoryAsync(),
            IncomeExpense = await _reportsService.GetIncomeExpenseSummaryAsync(selectedDate, selectedDate),
            TopMerchants = await _reportsService.GetTopMerchantsAsync(selectedDate, selectedDate)
        };

        return View(vm);
    }
}
