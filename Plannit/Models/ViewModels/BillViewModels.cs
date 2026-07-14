using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Models.ViewModels;

public static class BillDisplay
{
    public static string CadenceLabel(this RecurringCadence cadence) => RecurringDetectionService.CadenceLabel(cadence);

    public static bool IsOverdue(this Bill bill) => bill.IsActive && bill.NextDue < DateOnly.FromDateTime(DateTime.Today);
}

public class BillIndexViewModel
{
    public List<Bill> Bills { get; set; } = new();
    public ForecastResult? Forecast { get; set; }
    public List<Bill> ExpenseBills => Bills.Where(b => b.IsActive && !b.IsIncome).ToList();
    public List<Bill> IncomeBills => Bills.Where(b => b.IsActive && b.IsIncome).ToList();
    public List<Bill> DismissedBills => Bills.Where(b => !b.IsActive).ToList();
    public decimal MonthlyBillTotal => ExpenseBills
        .Where(b => b.Cadence == RecurringCadence.Monthly)
        .Sum(b => b.ExpectedAmount);
    public int OverdueCount => Bills.Count(b => b.IsOverdue());
}

public class BillFormViewModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public RecurringCadence Cadence { get; set; } = RecurringCadence.Monthly;
    public decimal ExpectedAmount { get; set; }
    public DateOnly NextDue { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool IsIncome { get; set; }
}

public class CalendarViewModel
{
    public DateOnly Month { get; set; }
    public string MonthLabel => Month.ToString("MMMM yyyy");
    public List<CalendarDayViewModel> Days { get; set; } = new();
    public List<BillOccurrence> UpcomingList => Days
        .Where(d => d.IsCurrentMonth || d.Date >= DateOnly.FromDateTime(DateTime.Today))
        .SelectMany(d => d.Occurrences)
        .OrderBy(o => o.Date)
        .ToList();
}

public class CalendarDayViewModel
{
    public DateOnly Date { get; set; }
    public bool IsCurrentMonth { get; set; }
    public bool IsToday { get; set; }
    public List<BillOccurrence> Occurrences { get; set; } = new();
}
