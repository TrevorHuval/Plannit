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
        Assert.Equal(AmountNature.Fixed, results[0].Nature);
        Assert.Equal(15.99m, results[0].AverageAmount);
        Assert.False(results[0].IsIncome);
    }

    [Fact]
    public void DetectsWeeklySubscription()
    {
        var transactions = CreateIntervalTransactions("Weekly Service", -25.00m, new DateOnly(2026, 1, 5), 7, 4);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Weekly, results[0].Cadence);
        Assert.Equal(25.00m, results[0].AverageAmount);
    }

    [Fact]
    public void DetectsBiweeklySubscription()
    {
        var transactions = CreateIntervalTransactions("Meal Kit", -60.00m, new DateOnly(2026, 1, 2), 14, 4);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Biweekly, results[0].Cadence);
    }

    [Fact]
    public void DetectsQuarterlySubscription()
    {
        var transactions = CreateIntervalTransactions("Insurance Premium", -300.00m, new DateOnly(2026, 1, 1), 91, 4);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Quarterly, results[0].Cadence);
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
    public void DetectsYearlySubscriptionWithOnlyTwoOccurrences()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2025, 3, 10), Amount = -49.00m, Description = "Domain Renewal" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 3, 12), Amount = -49.00m, Description = "Domain Renewal" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Yearly, results[0].Cadence);
        Assert.Equal(2, results[0].OccurrenceCount);
    }

    [Fact]
    public void TwoOccurrencesOfNonYearlyCadenceAreNotDetected()
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
    public void ToleratesOneMissedMonth()
    {
        // Netflix billed Jan/Feb/Apr/May/Jun — March was skipped (card declined, plan paused, etc.)
        var dates = new[]
        {
            new DateOnly(2026, 1, 15), new DateOnly(2026, 2, 15), new DateOnly(2026, 4, 15),
            new DateOnly(2026, 5, 15), new DateOnly(2026, 6, 15)
        };
        var transactions = dates.Select((d, i) => new Transaction
        {
            Id = i + 1,
            AccountId = 1,
            Date = d,
            Amount = -15.99m,
            Description = "Netflix"
        }).ToList();

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Monthly, results[0].Cadence);
        Assert.Equal(5, results[0].OccurrenceCount);
        Assert.Equal(AmountNature.Fixed, results[0].Nature);
    }

    [Fact]
    public void DetectsVariableAmountUtilityBill()
    {
        var dates = new[]
        {
            new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)
        };
        var amounts = new[] { -100.00m, -120.00m, -90.00m, -110.00m };
        var transactions = dates.Select((d, i) => new Transaction
        {
            Id = i + 1,
            AccountId = 1,
            Date = d,
            Amount = amounts[i],
            Description = "City Electric Co"
        }).ToList();

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(RecurringCadence.Monthly, results[0].Cadence);
        Assert.Equal(AmountNature.Variable, results[0].Nature);
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
    public void IgnoresAmountsBeyondTolerance()
    {
        var transactions = CreateMonthlyTransactions("Varying Charge", -10m, new DateOnly(2026, 1, 15), 3);
        transactions[1].Amount = -50m;
        transactions[2].Amount = -100m;

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
        Assert.Equal(AmountNature.Fixed, results[0].Nature);
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
    public void GroupsAmazonChargesDespiteNoiseSuffixes()
    {
        var dates = new[] { new DateOnly(2026, 1, 8), new DateOnly(2026, 2, 9), new DateOnly(2026, 3, 7) };
        var suffixes = new[] { "*RC3GY2873", "*AB19KTQ4Z", "*ZZ99QW1234" };
        var transactions = dates.Select((d, i) => new Transaction
        {
            Id = i + 1,
            AccountId = 1,
            Date = d,
            Amount = -45.60m,
            Description = $"Amazon.com{suffixes[i]}"
        }).ToList();

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.Equal(3, results[0].OccurrenceCount);
    }

    [Fact]
    public void DetectsRecurringIncome()
    {
        var transactions = CreateIntervalTransactions("Direct Deposit Payroll", 1500.00m, new DateOnly(2026, 1, 2), 14, 4);
        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Single(results);
        Assert.True(results[0].IsIncome);
        Assert.Equal(RecurringCadence.Biweekly, results[0].Cadence);
    }

    [Fact]
    public void DoesNotGroupMixedSignTransactionsTogether()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 15), Amount = -50m, Description = "Refund Adjustments" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 2, 15), Amount = 50m, Description = "Refund Adjustments" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 3, 15), Amount = -50m, Description = "Refund Adjustments" },
        };

        var results = RecurringDetectionService.DetectFromTransactions(transactions);

        Assert.Empty(results);
    }

    [Fact]
    public void IgnoresNonRecurringControlGroup()
    {
        var transactions = new List<Transaction>
        {
            new() { Id = 1, AccountId = 1, Date = new DateOnly(2026, 1, 3), Amount = -212.40m, Description = "Best Buy" },
            new() { Id = 2, AccountId = 1, Date = new DateOnly(2026, 1, 9), Amount = -18.22m, Description = "Local Diner" },
            new() { Id = 3, AccountId = 1, Date = new DateOnly(2026, 2, 27), Amount = -64.10m, Description = "REI Co-op" },
            new() { Id = 4, AccountId = 1, Date = new DateOnly(2026, 3, 4), Amount = -9.50m, Description = "Corner Bakery" },
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

    private static List<Transaction> CreateIntervalTransactions(string description, decimal amount, DateOnly startDate, int intervalDays, int count)
    {
        var transactions = new List<Transaction>();
        for (int i = 0; i < count; i++)
        {
            transactions.Add(new Transaction
            {
                Id = i + 1,
                AccountId = 1,
                Date = startDate.AddDays(i * intervalDays),
                Amount = amount,
                Description = description
            });
        }
        return transactions;
    }
}
