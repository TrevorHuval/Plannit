using Plannit.Services;

namespace Plannit.Tests;

public class ForecastServiceTests
{
    private static readonly DateOnly Today = new(2026, 7, 13);

    [Fact]
    public void ComputeForecast_NoBills_AppliesOnlyDiscretionaryDrift()
    {
        var result = ForecastService.ComputeForecast(
            startingBalance: 1000m,
            today: Today,
            days: 5,
            billOccurrences: [],
            avgDailyDiscretionary: -10m);

        Assert.Equal(5, result.Points.Count);
        Assert.Equal(990m, result.Points[0].Balance);
        Assert.Equal(950m, result.Points[4].Balance);
        Assert.Null(result.ZeroCrossingDate);
    }

    [Fact]
    public void ComputeForecast_BillOccurrence_AppliesOnItsDate()
    {
        var billDate = Today.AddDays(3);
        var result = ForecastService.ComputeForecast(
            startingBalance: 1000m,
            today: Today,
            days: 5,
            billOccurrences: [(billDate, -200m)],
            avgDailyDiscretionary: 0m);

        Assert.Equal(1000m, result.Points[1].Balance); // day 2, before the bill
        Assert.Equal(800m, result.Points[2].Balance);  // day 3, bill lands
        Assert.Equal(800m, result.Points[4].Balance);  // stays down afterward
    }

    [Fact]
    public void ComputeForecast_MultipleBillsSameDay_Sum()
    {
        var billDate = Today.AddDays(1);
        var result = ForecastService.ComputeForecast(
            startingBalance: 500m,
            today: Today,
            days: 2,
            billOccurrences: [(billDate, -100m), (billDate, 50m)],
            avgDailyDiscretionary: 0m);

        Assert.Equal(450m, result.Points[0].Balance);
    }

    [Fact]
    public void ComputeForecast_DetectsFirstZeroCrossing()
    {
        var result = ForecastService.ComputeForecast(
            startingBalance: 100m,
            today: Today,
            days: 10,
            billOccurrences: [],
            avgDailyDiscretionary: -30m);

        // 100 -> 70 -> 40 -> 10 -> -20 (day 4 crosses zero)
        Assert.Equal(Today.AddDays(4), result.ZeroCrossingDate);
    }

    [Fact]
    public void ComputeForecast_NeverCrossesZero_ReturnsNull()
    {
        var result = ForecastService.ComputeForecast(
            startingBalance: 10000m,
            today: Today,
            days: 30,
            billOccurrences: [(Today.AddDays(5), -500m)],
            avgDailyDiscretionary: -5m);

        Assert.Null(result.ZeroCrossingDate);
    }
}
