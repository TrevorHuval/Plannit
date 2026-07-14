using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Controllers;

[Authorize]
public class BillsController : Controller
{
    private readonly BillService _billService;
    private readonly ForecastService _forecastService;

    public BillsController(BillService billService, ForecastService forecastService)
    {
        _billService = billService;
        _forecastService = forecastService;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public async Task<IActionResult> Index()
    {
        await _billService.ReconcileAsync();
        var bills = await _billService.GetAllAsync();
        var forecast = await _forecastService.GetForecastAsync(90);
        return View(new BillIndexViewModel { Bills = bills, Forecast = forecast });
    }

    public async Task<IActionResult> Calendar(int? year, int? month)
    {
        await _billService.ReconcileAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(year ?? today.Year, month ?? today.Month, 1);
        var vm = await BuildCalendarViewModelAsync(monthStart);
        return View(vm);
    }

    public IActionResult Create() => View("Edit", new BillFormViewModel());

    public async Task<IActionResult> Edit(int id)
    {
        var bill = await _billService.GetByIdAsync(id);
        if (bill is null) return NotFound();

        return View(new BillFormViewModel
        {
            Id = bill.Id,
            Name = bill.Name,
            Cadence = bill.Cadence,
            ExpectedAmount = bill.ExpectedAmount,
            NextDue = bill.NextDue,
            IsIncome = bill.IsIncome
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BillFormViewModel model)
    {
        if (!ModelState.IsValid) return View("Edit", model);

        if (model.Id.HasValue)
            await _billService.UpdateAsync(model.Id.Value, model.Name, model.Cadence, model.ExpectedAmount, model.NextDue, model.IsIncome);
        else
            await _billService.CreateAsync(UserId, model.Name, model.Cadence, model.ExpectedAmount, model.NextDue, model.IsIncome);

        TempData["Success"] = "Bill saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(int id)
    {
        await _billService.DismissAsync(id);
        TempData["Success"] = "Bill dismissed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await _billService.DeleteAsync(id);
        TempData["Success"] = "Bill deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Promote(string description, RecurringCadence cadence, decimal averageAmount, DateOnly nextExpected, bool isIncome)
    {
        await _billService.PromoteAsync(UserId, description, cadence, averageAmount, nextExpected, isIncome);
        TempData["Success"] = $"\"{description}\" is now tracked as a bill.";
        return RedirectToAction("Recurring", "Budgets");
    }

    private async Task<CalendarViewModel> BuildCalendarViewModelAsync(DateOnly monthStart)
    {
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var gridStart = monthStart.AddDays(-(int)monthStart.DayOfWeek);
        var gridEnd = monthEnd.AddDays(6 - (int)monthEnd.DayOfWeek);

        var occurrences = await _billService.GetOccurrencesAsync(gridStart, gridEnd);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var days = new List<CalendarDayViewModel>();
        for (var d = gridStart; d <= gridEnd; d = d.AddDays(1))
        {
            days.Add(new CalendarDayViewModel
            {
                Date = d,
                IsCurrentMonth = d.Month == monthStart.Month && d.Year == monthStart.Year,
                IsToday = d == today,
                Occurrences = occurrences.Where(o => o.Date == d).ToList()
            });
        }

        return new CalendarViewModel { Month = monthStart, Days = days };
    }
}
