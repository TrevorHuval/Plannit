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

    public async Task<IActionResult> Index(DateOnly? startDate, DateOnly? endDate, string? preset, string? nav)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var resolvedPreset = preset;

        if (!string.IsNullOrEmpty(nav) && startDate.HasValue && endDate.HasValue)
        {
            var rangeLength = endDate.Value.DayNumber - startDate.Value.DayNumber + 1;
            if (nav == "prev")
            {
                endDate = startDate.Value.AddDays(-1);
                startDate = endDate.Value.AddDays(-(rangeLength - 1));
            }
            else if (nav == "next")
            {
                startDate = endDate.Value.AddDays(1);
                endDate = startDate.Value.AddDays(rangeLength - 1);
            }
            resolvedPreset = "custom";
        }
        else if (!string.IsNullOrEmpty(resolvedPreset))
        {
            (startDate, endDate) = ResolvePreset(resolvedPreset, today);
        }
        else if (startDate.HasValue && endDate.HasValue)
        {
            resolvedPreset = "custom";
        }
        else
        {
            resolvedPreset = "thisMonth";
            (startDate, endDate) = ResolvePreset(resolvedPreset, today);
        }

        var vm = new ReportsViewModel
        {
            StartDate = startDate!.Value,
            EndDate = endDate!.Value,
            Preset = resolvedPreset,
            SpendByCategory = await _reportsService.GetSpendByCategoryAsync(startDate!.Value, endDate!.Value),
            MonthlyHistory = await _reportsService.GetMonthlySpendHistoryAsync(),
            IncomeExpense = await _reportsService.GetIncomeExpenseSummaryAsync(startDate!.Value, endDate!.Value),
            TopMerchants = await _reportsService.GetTopMerchantsAsync(startDate!.Value, endDate!.Value)
        };

        return View(vm);
    }

    private static (DateOnly start, DateOnly end) ResolvePreset(string preset, DateOnly today)
    {
        return preset switch
        {
            "thisMonth" => (new DateOnly(today.Year, today.Month, 1),
                            new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            "lastMonth" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1),
                            new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
            "last3Months" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-2),
                              new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            "ytd" => (new DateOnly(today.Year, 1, 1), today),
            "last12Months" => (today.AddMonths(-12).AddDays(1), today),
            "allTime" => (DateOnly.MinValue, DateOnly.MaxValue),
            _ => (new DateOnly(today.Year, today.Month, 1),
                  new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1))
        };
    }
}
