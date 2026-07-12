using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class RecurringDetectionTests
{
    [Fact]
    public void DetectsMonthlySubscription()
    {
        var transactions = CreateMonthlyTransactions("Netflix", -15.99m, new DateOnly(2026, 1, 15), 4);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal("Netflix", results[0].Description);
        Assert.Equal(RecurringCadence.Monthly, results[0].Cadence);
        Assert.Equal(15.99m, results[0].AverageAmount);
    }

    [Fact]
    public void DetectsWeeklySubscription()
    {
        var baseDate = new DateOnly(2026, 1, 5);
        var transactions = new List<Transaction>();
        for (int i = 0; i < 4; i++)
        {
            transactions.Add(new Transaction
            {
                Id = i + 1,
                AccountId = 1,
                Date = baseDate.AddDays(i * 7),
                Amount = -25.00m,
                Description = "Weekly Service"
            });
        }

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Weekly, results[0].Cadence);
        Assert.Equal(25.00m, results[0].AverageAmount);
    }

    [Fact]
    public void DetectsYearlySubscription()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2023, 3, 10), Amount = -99.99m, Description = "Annual License" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2024, 3, 12), Amount = -99.99m, Description = "Annual License" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2025, 3, 9), Amount = -99.99m, Description = "Annual License" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Yearly, results[0].Cadence);
        Assert.Equal(99.99m, results[0].AverageAmount);
    }

    [Fact]
    public void IgnoresIrregularTransactions()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 5), Amount = -50m, Description = "Random Store" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 1, 20), Amount = -50m, Description = "Random Store" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 1), Amount = -50m, Description = "Random Store" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Empty(results);
    }

    [Fact]
    public void IgnoresVariableAmounts()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 15), Amount = -10m, Description = "Varying Charge" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 15), Amount = -50m, Description = "Varying Charge" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 15), Amount = -100m, Description = "Varying Charge" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Empty(results);
    }

    [Fact]
    public void ToleratesSmallAmountVariation()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 1), Amount = -10.00m, Description = "Spotify" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 1), Amount = -10.50m, Description = "Spotify" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 1), Amount = -9.99m, Description = "Spotify" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Monthly, results[0].Cadence);
        Assert.Equal("Spotify", results[0].Description);
    }

    [Fact]
    public void ToleratesDateJitter()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 15), Amount = -85.00m, Description = "AT&T Wireless" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 13), Amount = -85.00m, Description = "AT&T Wireless" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 17), Amount = -85.00m, Description = "AT&T Wireless" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Monthly, results[0].Cadence);
    }

    [Fact]
    public void CalculatesAnnualizedCost()
    {
        var transactions = CreateMonthlyTransactions("Netflix", -15.99m, new DateOnly(2026, 1, 15), 3);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(15.99m * 12, results[0].AnnualizedCost);
    }

    [Fact]
    public void CalculatesNextExpectedDate()
    {
        var transactions = CreateMonthlyTransactions("Netflix", -15.99m, new DateOnly(2026, 1, 15), 3);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(new DateOnly(2026, 4, 15), results[0].NextExpected);
    }

    [Fact]
    public void DetectsMultipleRecurringGroups()
    {
        var transactions = new List<Transaction>();
        transactions.AddRange(CreateMonthlyTransactions("Netflix", -15.99m, new DateOnly(2026, 1, 10), 3));
        transactions.AddRange(CreateMonthlyTransactions("Spotify", -9.99m, new DateOnly(2026, 1, 5), 3));

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Description == "Netflix");
        Assert.Contains(results, r => r.Description == "Spotify");
    }

    [Fact]
    public void RequiresAtLeastThreeOccurrences()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 15), Amount = -15.99m, Description = "NewService" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 15), Amount = -15.99m, Description = "NewService" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Empty(results);
    }

    [Fact]
    public void IgnoresPositiveAmounts()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 1), Amount = 3200m, Description = "Payroll" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 1), Amount = 3200m, Description = "Payroll" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 1), Amount = 3200m, Description = "Payroll" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Empty(results);
    }

    private static List<Transaction> CreateMonthlyTransactions(string description, decimal amount, DateOnly startDate, int count)
    {
        var transactions = new List<Transaction>();
        for (int i = 0; i < count; i++)
        {
            transactions.Add(new Transaction
            {
                Id = i + 1,
                AccountId = 1,
                Date = startDate.AddMonths(i),
                Amount = amount,
                Description = description
            });
        }
        return transactions;
    }
}
