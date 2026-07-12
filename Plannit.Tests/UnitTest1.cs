using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class ProjectionServiceTests
{
    [Fact]
    public void SingleAccount_FixedRate_NoContributions_GrowsCorrectly()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1990,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "Brokerage",
                    AccountType = AccountType.Brokerage,
                    CurrentBalance = 100_000,
                    AnnualContribution = 0,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0.10m,
                    ContributionEndAge = 65
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        Assert.Equal(100_000m, result.YearlySnapshots[0].TotalBalance);

        // Year 1: 100000 * 1.10 = 110000
        Assert.Equal(110_000m, result.YearlySnapshots[1].TotalBalance);

        // Year 2: 110000 * 1.10 = 121000
        Assert.Equal(121_000m, result.YearlySnapshots[2].TotalBalance);
    }

    [Fact]
    public void SingleAccount_WithContributions_AccumulatesCorrectly()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "401k",
                    AccountType = AccountType.Retirement401k,
                    CurrentBalance = 50_000,
                    AnnualContribution = 10_000,
                    EmployerMatch = 5_000,
                    ExpectedReturnRate = 0.10m,
                    ContributionEndAge = 65
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // Year 0: 50000 (initial)
        Assert.Equal(50_000m, result.YearlySnapshots[0].TotalBalance);

        // Year 1: 50000 * 1.10 + 10000 + 5000 = 70000
        Assert.Equal(70_000m, result.YearlySnapshots[1].TotalBalance);

        // Year 2: 70000 * 1.10 + 15000 = 92000
        Assert.Equal(92_000m, result.YearlySnapshots[2].TotalBalance);
    }

    [Fact]
    public void RetirementWithdrawals_DepletesBalance()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 100,
            AnnualRetirementSpending = 50_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "Savings",
                    AccountType = AccountType.Savings,
                    CurrentBalance = 200_000,
                    AnnualContribution = 0,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0,
                    ContributionEndAge = 60
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // At age 60, balance = 200000; 0% return, $50k/yr spending
        // Withdrawals at ages 61-64 drain to 0; at age 65, withdrawal fails
        Assert.NotNull(result.DepletionAge);
        Assert.Equal(65, result.DepletionAge);
    }

    [Fact]
    public void SufficientBalance_NeverDepletes()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 30_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "Brokerage",
                    AccountType = AccountType.Brokerage,
                    CurrentBalance = 2_000_000,
                    AnnualContribution = 0,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0.05m,
                    ContributionEndAge = 60
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        Assert.Null(result.DepletionAge);
        Assert.True(result.SurplusAtDeath > 0);
    }

    [Fact]
    public void Inflation_IncreasesSpendingOverTime()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 100,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "Savings",
                    AccountType = AccountType.Savings,
                    CurrentBalance = 300_000,
                    AnnualContribution = 0,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0,
                    ContributionEndAge = 60
                }
            ]
        };

        var resultWithInflation = ProjectionService.RunProjection(input);

        input.InflationRate = 0;
        var resultNoInflation = ProjectionService.RunProjection(input);

        // Inflation should cause faster depletion
        Assert.True(resultWithInflation.DepletionAge < resultNoInflation.DepletionAge);
    }

    [Fact]
    public void WithdrawOrder_TaxableFirst_ThenTraditional_ThenRoth()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1961,
            RetirementAge = 65,
            LifeExpectancy = 80,
            AnnualRetirementSpending = 100_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Checking", AccountType = AccountType.Checking,
                    CurrentBalance = 50_000, ExpectedReturnRate = 0, ContributionEndAge = 65
                },
                new AccountAssumptionInput
                {
                    AccountId = 2, AccountName = "Brokerage", AccountType = AccountType.Brokerage,
                    CurrentBalance = 50_000, ExpectedReturnRate = 0, ContributionEndAge = 65
                },
                new AccountAssumptionInput
                {
                    AccountId = 3, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 50_000, ExpectedReturnRate = 0, ContributionEndAge = 65
                },
                new AccountAssumptionInput
                {
                    AccountId = 4, AccountName = "Roth IRA", AccountType = AccountType.RothIra,
                    CurrentBalance = 50_000, ExpectedReturnRate = 0, ContributionEndAge = 65
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // After first year of retirement (age 66), $100k withdrawn
        // Checking (50k) drained first, then Brokerage partially
        var yearAfterRetirement = result.YearlySnapshots.First(s => s.Age == 66);
        Assert.Equal(0m, yearAfterRetirement.AccountBalances[1]); // Checking drained
        Assert.Equal(0m, yearAfterRetirement.AccountBalances[2]); // Brokerage drained
        Assert.Equal(50_000m, yearAfterRetirement.AccountBalances[3]); // 401k untouched
        Assert.Equal(50_000m, yearAfterRetirement.AccountBalances[4]); // Roth untouched
    }

    [Fact]
    public void ContributionEndAge_StopsContributionsAtSpecifiedAge()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "401k",
                    AccountType = AccountType.Retirement401k,
                    CurrentBalance = 0,
                    AnnualContribution = 10_000,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0,
                    ContributionEndAge = 33
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // Contributions happen when age < ContributionEndAge (33)
        Assert.Equal(0m, result.YearlySnapshots[0].TotalBalance); // age 30, initial
        Assert.Equal(10_000m, result.YearlySnapshots[1].TotalBalance); // age 31, +10k
        Assert.Equal(20_000m, result.YearlySnapshots[2].TotalBalance); // age 32, +10k (32 < 33)
        Assert.Equal(20_000m, result.YearlySnapshots[3].TotalBalance); // age 33, no contribution (33 not < 33)
    }

    [Fact]
    public void SafeSpending_Is4PercentOfRetirementBalance()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 20_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1,
                    AccountName = "Savings",
                    AccountType = AccountType.Savings,
                    CurrentBalance = 1_000_000,
                    AnnualContribution = 0,
                    EmployerMatch = 0,
                    ExpectedReturnRate = 0,
                    ContributionEndAge = 60
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        Assert.Equal(40_000m, result.SafeSpendingEstimate);
    }

    [Fact]
    public void MultipleAccounts_TotalBalanceIsSumOfAccounts()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Savings", AccountType = AccountType.Savings,
                    CurrentBalance = 10_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 65
                },
                new AccountAssumptionInput
                {
                    AccountId = 2, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 20_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 65
                }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        Assert.Equal(30_000m, result.YearlySnapshots[0].TotalBalance);
    }

    [Fact]
    public void LifeEvent_OneTime_IncreasesBalance()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 30_000,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Savings", AccountType = AccountType.Savings,
                    CurrentBalance = 100_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 65
                }
            ],
            LifeEvents =
            [
                new LifeEventInput { Name = "Sell house", Age = 62, Amount = 200_000, IsRecurring = false }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // At age 62 (year 2), balance should be 100k + 200k = 300k
        var snap62 = result.YearlySnapshots.First(s => s.Age == 62);
        Assert.Equal(300_000m, snap62.TotalBalance);
    }

    [Fact]
    public void LifeEvent_Recurring_AppliesEachYear()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 70,
            LifeExpectancy = 75,
            AnnualRetirementSpending = 0,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Savings", AccountType = AccountType.Savings,
                    CurrentBalance = 100_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 70
                }
            ],
            LifeEvents =
            [
                new LifeEventInput { Name = "College", Age = 62, Amount = -30_000, IsRecurring = true, EndAge = 64 }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // Age 61 (year 1): no event, balance = 100k
        Assert.Equal(100_000m, result.YearlySnapshots.First(s => s.Age == 61).TotalBalance);
        // Age 62 (year 2): -30k => 70k
        Assert.Equal(70_000m, result.YearlySnapshots.First(s => s.Age == 62).TotalBalance);
        // Age 63 (year 3): -30k => 40k
        Assert.Equal(40_000m, result.YearlySnapshots.First(s => s.Age == 63).TotalBalance);
        // Age 64 (year 4): -30k => 10k
        Assert.Equal(10_000m, result.YearlySnapshots.First(s => s.Age == 64).TotalBalance);
        // Age 65 (year 5): no event (65 > endAge 64)
        Assert.Equal(10_000m, result.YearlySnapshots.First(s => s.Age == 65).TotalBalance);
    }

    [Fact]
    public void LifeEvent_Negative_WithdrawsUsingOrder()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 70,
            LifeExpectancy = 75,
            AnnualRetirementSpending = 0,
            InflationRate = 0,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Checking", AccountType = AccountType.Checking,
                    CurrentBalance = 20_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 70
                },
                new AccountAssumptionInput
                {
                    AccountId = 2, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 100_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0, ContributionEndAge = 70
                }
            ],
            LifeEvents =
            [
                new LifeEventInput { Name = "Big expense", Age = 61, Amount = -50_000, IsRecurring = false }
            ]
        };

        var result = ProjectionService.RunProjection(input);

        // Age 61: -50k withdrawn. Checking (20k) drained first, then 30k from 401k
        var snap = result.YearlySnapshots.First(s => s.Age == 61);
        Assert.Equal(0m, snap.AccountBalances[1]);
        Assert.Equal(70_000m, snap.AccountBalances[2]);
    }

    [Fact]
    public void MonteCarlo_FixedSeed_ProducesConsistentResults()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 500_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 60
                }
            ],
            LifeEvents = []
        };

        var result1 = ProjectionService.RunMonteCarlo(input, 0.15m, 1000, 42);
        var result2 = ProjectionService.RunMonteCarlo(input, 0.15m, 1000, 42);

        // Same seed => identical results
        Assert.Equal(result1.SuccessProbability, result2.SuccessProbability);
        Assert.Equal(result1.PercentileBands.Count, result2.PercentileBands.Count);

        for (int i = 0; i < result1.PercentileBands.Count; i++)
        {
            Assert.Equal(result1.PercentileBands[i].Values[50], result2.PercentileBands[i].Values[50]);
        }
    }

    [Fact]
    public void MonteCarlo_SuccessProbability_IsReasonable()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 2_000_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 60
                }
            ],
            LifeEvents = []
        };

        var result = ProjectionService.RunMonteCarlo(input, 0.15m, 1000, 42);

        // With $2M and only $40k/yr spending at 7% return, should have very high success
        Assert.True(result.SuccessProbability > 90);
    }

    [Fact]
    public void MonteCarlo_PercentileBands_AreOrdered()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 100_000, AnnualContribution = 20_000, EmployerMatch = 5_000,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 65
                }
            ],
            LifeEvents = []
        };

        var result = ProjectionService.RunMonteCarlo(input, 0.15m, 1000, 42);

        // Percentiles should be ordered: p10 <= p25 <= p50 <= p75 <= p90
        foreach (var band in result.PercentileBands)
        {
            Assert.True(band.Values[10] <= band.Values[25]);
            Assert.True(band.Values[25] <= band.Values[50]);
            Assert.True(band.Values[50] <= band.Values[75]);
            Assert.True(band.Values[75] <= band.Values[90]);
        }
    }

    [Fact]
    public void MonteCarlo_WithLifeEvents_ShiftsOutcomes()
    {
        var baseInput = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 50_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 800_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 60
                }
            ],
            LifeEvents = []
        };

        var withEventInput = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1966,
            RetirementAge = 60,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 50_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 800_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 60
                }
            ],
            LifeEvents =
            [
                new LifeEventInput { Name = "Big expense", Age = 65, Amount = -200_000, IsRecurring = false }
            ]
        };

        var resultBase = ProjectionService.RunMonteCarlo(baseInput, 0.15m, 1000, 42);
        var resultWithEvent = ProjectionService.RunMonteCarlo(withEventInput, 0.15m, 1000, 42);

        // A $200k expense should reduce success probability
        Assert.True(resultWithEvent.SuccessProbability < resultBase.SuccessProbability);
    }

    [Fact]
    public void Fire_ComputesCorrectFireNumber()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 50_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "401k", AccountType = AccountType.Retirement401k,
                    CurrentBalance = 200_000, AnnualContribution = 20_000, EmployerMatch = 5_000,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 65
                }
            ],
            LifeEvents = []
        };

        var fire = ProjectionService.ComputeFire(input);

        Assert.Equal(1_250_000m, fire.FireNumber); // 25 * 50000
        Assert.Equal(200_000m, fire.CurrentTotal);
        Assert.Equal(16.0m, fire.ProgressPercent); // 200k / 1.25M * 100
        Assert.NotNull(fire.ProjectedFireAge);
        Assert.True(fire.ProjectedFireAge > 30); // should be reachable
    }

    [Fact]
    public void Fire_AlreadyReached_ReturnsCurrentAge()
    {
        var input = new ProjectionInput
        {
            CurrentYear = 2026,
            BirthYear = 1996,
            RetirementAge = 65,
            LifeExpectancy = 90,
            AnnualRetirementSpending = 40_000,
            InflationRate = 0.03m,
            AccountAssumptions =
            [
                new AccountAssumptionInput
                {
                    AccountId = 1, AccountName = "Brokerage", AccountType = AccountType.Brokerage,
                    CurrentBalance = 2_000_000, AnnualContribution = 0, EmployerMatch = 0,
                    ExpectedReturnRate = 0.07m, ContributionEndAge = 65
                }
            ],
            LifeEvents = []
        };

        var fire = ProjectionService.ComputeFire(input);

        Assert.Equal(1_000_000m, fire.FireNumber); // 25 * 40000
        Assert.Equal(2_000_000m, fire.CurrentTotal);
        Assert.True(fire.ProgressPercent >= 100);
        Assert.Equal(30, fire.ProjectedFireAge); // current age
    }
}
