using Plannit.Services;

namespace Plannit.Tests;

public class LoanServiceTests
{
    private static readonly DateOnly Today = new(2026, 7, 13);

    // ===== Amortization (hand-calculated) =====

    [Fact]
    public void RunAmortizationSchedule_HandCalculatedLoan_MatchesExpectedRows()
    {
        // $1,000 at 12% APR (1%/mo), $500/mo payment.
        // Mo1: interest 10.00, principal 490.00, balance 510.00
        // Mo2: interest 5.10, principal 494.90, balance 15.10
        // Mo3: interest 0.15, principal capped at remaining 15.10, balance 0.00
        var schedule = LoanService.RunAmortizationSchedule(principal: 1000m, annualRate: 0.12m, monthlyPayment: 500m);

        Assert.Equal(3, schedule.Count);

        Assert.Equal(1, schedule[0].Month);
        Assert.Equal(10.00m, schedule[0].Interest);
        Assert.Equal(490.00m, schedule[0].Principal);
        Assert.Equal(510.00m, schedule[0].RemainingBalance);

        Assert.Equal(5.10m, schedule[1].Interest);
        Assert.Equal(494.90m, schedule[1].Principal);
        Assert.Equal(15.10m, schedule[1].RemainingBalance);

        Assert.Equal(0.15m, schedule[2].Interest);
        Assert.Equal(15.10m, schedule[2].Principal);
        Assert.Equal(15.25m, schedule[2].Payment);
        Assert.Equal(0m, schedule[2].RemainingBalance);
    }

    [Fact]
    public void RunAmortizationSchedule_PaymentBelowInterest_NeverPaysDown()
    {
        // $1,000 at 24% APR (2%/mo) accrues $20/mo interest; a $15 payment can't cover it.
        var schedule = LoanService.RunAmortizationSchedule(principal: 1000m, annualRate: 0.24m, monthlyPayment: 15m);

        Assert.Empty(schedule);
    }

    // ===== Avalanche vs snowball ordering =====

    [Fact]
    public void SimulatePayoff_Avalanche_PrioritizesHighestRateFirst()
    {
        var debts = new List<DebtAccountInput>
        {
            new() { AccountId = 1, Name = "SmallHighRate", Balance = 300m, AnnualRate = 0.24m, MinimumPayment = 10m },
            new() { AccountId = 2, Name = "BigLowRate", Balance = 1000m, AnnualRate = 0.05m, MinimumPayment = 20m }
        };

        var result = LoanService.SimulatePayoff(debts, extraPayment: 100m, DebtOrderStrategy.Avalanche, Today);

        Assert.Equal("SmallHighRate", result.Accounts[0].Name);
        Assert.Equal("BigLowRate", result.Accounts[1].Name);
    }

    [Fact]
    public void SimulatePayoff_Snowball_PrioritizesSmallestBalanceFirst()
    {
        var debts = new List<DebtAccountInput>
        {
            new() { AccountId = 1, Name = "SmallHighRate", Balance = 300m, AnnualRate = 0.24m, MinimumPayment = 10m },
            new() { AccountId = 2, Name = "BigLowRate", Balance = 1000m, AnnualRate = 0.05m, MinimumPayment = 20m }
        };

        var result = LoanService.SimulatePayoff(debts, extraPayment: 100m, DebtOrderStrategy.Snowball, Today);

        Assert.Equal("SmallHighRate", result.Accounts[0].Name); // also the smaller balance here
        Assert.Equal("BigLowRate", result.Accounts[1].Name);
    }

    [Fact]
    public void SimulatePayoff_Snowball_PrioritizesSmallestBalance_EvenWithLowerRate()
    {
        var debts = new List<DebtAccountInput>
        {
            new() { AccountId = 1, Name = "SmallLowRate", Balance = 200m, AnnualRate = 0.05m, MinimumPayment = 10m },
            new() { AccountId = 2, Name = "BigHighRate", Balance = 1000m, AnnualRate = 0.24m, MinimumPayment = 20m }
        };

        var avalanche = LoanService.SimulatePayoff(debts, extraPayment: 100m, DebtOrderStrategy.Avalanche, Today);
        var snowball = LoanService.SimulatePayoff(debts, extraPayment: 100m, DebtOrderStrategy.Snowball, Today);

        // Avalanche orders by rate: BigHighRate first.
        Assert.Equal("BigHighRate", avalanche.Accounts[0].Name);
        // Snowball orders by balance: SmallLowRate first, regardless of rate.
        Assert.Equal("SmallLowRate", snowball.Accounts[0].Name);
    }

    [Fact]
    public void CompareStrategies_AvalancheNeverPaysMoreInterestThanSnowball()
    {
        var debts = new List<DebtAccountInput>
        {
            new() { AccountId = 1, Name = "CardA", Balance = 4000m, AnnualRate = 0.22m, MinimumPayment = 80m },
            new() { AccountId = 2, Name = "CarLoan", Balance = 12000m, AnnualRate = 0.06m, MinimumPayment = 250m },
            new() { AccountId = 3, Name = "CardB", Balance = 800m, AnnualRate = 0.18m, MinimumPayment = 25m }
        };

        var comparison = LoanService.CompareStrategies(debts, extraPayment: 150m, Today);

        Assert.True(comparison.Avalanche.TotalInterestPaid <= comparison.Snowball.TotalInterestPaid);
        Assert.True(comparison.InterestSaved >= 0);
        Assert.True(comparison.MonthsSaved >= 0);

        // Every debt is eventually paid off (bounded loop didn't just give up).
        Assert.All(comparison.Avalanche.Accounts, a => Assert.True(a.MonthsToPayoff < 600));
        Assert.All(comparison.Snowball.Accounts, a => Assert.True(a.MonthsToPayoff < 600));
    }

    [Fact]
    public void SimulatePayoff_SingleDebtNoExtra_MatchesAmortizationScheduleLength()
    {
        var debts = new List<DebtAccountInput>
        {
            new() { AccountId = 1, Name = "Loan", Balance = 1000m, AnnualRate = 0.12m, MinimumPayment = 500m }
        };

        var result = LoanService.SimulatePayoff(debts, extraPayment: 0m, DebtOrderStrategy.Avalanche, Today);
        var schedule = LoanService.RunAmortizationSchedule(1000m, 0.12m, 500m);

        Assert.Equal(schedule.Count, result.TotalMonths);
        Assert.Equal(schedule.Count, result.Accounts[0].MonthsToPayoff);
    }
}
