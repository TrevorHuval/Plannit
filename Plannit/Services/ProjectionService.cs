using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class ProjectionService
{
    private readonly ApplicationDbContext _db;

    public ProjectionService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Account>> GetActiveAccountsAsync()
    {
        return await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .OrderBy(a => a.Type).ThenBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<ProjectionScenario?> GetScenarioAsync(int id)
    {
        return await _db.ProjectionScenarios
            .Include(s => s.AccountAssumptions)
                .ThenInclude(a => a.Account)
                    .ThenInclude(a => a.Snapshots)
            .Include(s => s.Events)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<ProjectionScenario>> GetScenariosAsync()
    {
        return await _db.ProjectionScenarios
            .Include(s => s.AccountAssumptions)
            .Include(s => s.Events)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<int> SaveScenarioAsync(ProjectionScenario scenario)
    {
        if (scenario.Id == 0)
            _db.ProjectionScenarios.Add(scenario);
        else
            _db.ProjectionScenarios.Update(scenario);

        await _db.SaveChangesAsync();
        return scenario.Id;
    }

    public async Task DeleteScenarioAsync(int id)
    {
        var scenario = await _db.ProjectionScenarios.FirstOrDefaultAsync(s => s.Id == id);
        if (scenario is not null)
        {
            _db.ProjectionScenarios.Remove(scenario);
            await _db.SaveChangesAsync();
        }
    }

    public static ProjectionResult RunProjection(ProjectionInput input)
    {
        var currentAge = input.CurrentYear - input.BirthYear;
        var yearsToProject = input.LifeExpectancy - currentAge;

        if (yearsToProject <= 0)
            return new ProjectionResult { YearlySnapshots = [], DepletionAge = currentAge, RetirementBalance = 0, SurplusAtDeath = 0 };

        var balances = new Dictionary<int, decimal>();
        foreach (var assumption in input.AccountAssumptions)
            balances[assumption.AccountId] = assumption.CurrentBalance;

        var snapshots = new List<YearlySnapshot>();
        decimal retirementBalance = 0;
        int? depletionAge = null;

        for (int year = 0; year <= yearsToProject; year++)
        {
            var age = currentAge + year;
            var isRetired = age >= input.RetirementAge;

            var snapshot = new YearlySnapshot
            {
                Year = input.CurrentYear + year,
                Age = age,
                AccountBalances = new Dictionary<int, decimal>()
            };

            if (year > 0)
            {
                foreach (var assumption in input.AccountAssumptions)
                {
                    var balance = balances[assumption.AccountId];

                    balance *= (1 + assumption.ExpectedReturnRate);

                    if (!isRetired && age < assumption.ContributionEndAge)
                        balance += assumption.AnnualContribution + assumption.EmployerMatch;

                    balances[assumption.AccountId] = Math.Max(0, balance);
                }

                ApplyLifeEvents(balances, input, age);

                if (isRetired && depletionAge is null)
                {
                    var inflationYears = age - input.RetirementAge;
                    var adjustedSpending = input.AnnualRetirementSpending *
                        (decimal)Math.Pow((double)(1 + input.InflationRate), inflationYears);

                    var remaining = adjustedSpending;
                    remaining = WithdrawFromAccounts(balances, input.AccountAssumptions, remaining, WithdrawOrder.TaxableFirst);

                    if (remaining > 0)
                        depletionAge = age;
                }
            }

            foreach (var assumption in input.AccountAssumptions)
                snapshot.AccountBalances[assumption.AccountId] = Math.Round(balances[assumption.AccountId], 2);

            snapshot.TotalBalance = snapshot.AccountBalances.Values.Sum();
            snapshots.Add(snapshot);

            if (age == input.RetirementAge)
                retirementBalance = snapshot.TotalBalance;
        }

        var finalBalance = snapshots.LastOrDefault()?.TotalBalance ?? 0;

        decimal safeSpending = 0;
        if (retirementBalance > 0)
        {
            var retirementYears = input.LifeExpectancy - input.RetirementAge;
            if (retirementYears > 0)
                safeSpending = Math.Round(retirementBalance * 0.04m, 2);
        }

        return new ProjectionResult
        {
            YearlySnapshots = snapshots,
            RetirementBalance = Math.Round(retirementBalance, 2),
            DepletionAge = depletionAge,
            SurplusAtDeath = depletionAge.HasValue ? 0 : Math.Round(finalBalance, 2),
            SafeSpendingEstimate = safeSpending
        };
    }

    public static MonteCarloResult RunMonteCarlo(ProjectionInput input, decimal stdDev, int iterations = 1000, int seed = 42)
    {
        var currentAge = input.CurrentYear - input.BirthYear;
        var yearsToProject = input.LifeExpectancy - currentAge;

        if (yearsToProject <= 0)
            return new MonteCarloResult { SuccessProbability = 0, PercentileBands = [] };

        var rng = new Random(seed);
        var allRuns = new decimal[iterations][];
        int successCount = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            allRuns[iter] = new decimal[yearsToProject + 1];
            var balances = new Dictionary<int, decimal>();
            foreach (var assumption in input.AccountAssumptions)
                balances[assumption.AccountId] = assumption.CurrentBalance;

            allRuns[iter][0] = balances.Values.Sum();
            bool depleted = false;

            for (int year = 1; year <= yearsToProject; year++)
            {
                var age = currentAge + year;
                var isRetired = age >= input.RetirementAge;

                foreach (var assumption in input.AccountAssumptions)
                {
                    var balance = balances[assumption.AccountId];
                    var accountStdDev = GetAccountStdDev(assumption.AccountType, stdDev);
                    var randomReturn = SampleNormalReturn(rng, (double)assumption.ExpectedReturnRate, (double)accountStdDev);
                    balance *= (1 + (decimal)randomReturn);
                    balance = Math.Max(0, balance);

                    if (!isRetired && age < assumption.ContributionEndAge)
                        balance += assumption.AnnualContribution + assumption.EmployerMatch;

                    balances[assumption.AccountId] = Math.Max(0, balance);
                }

                ApplyLifeEvents(balances, input, age);

                if (isRetired && !depleted)
                {
                    var inflationYears = age - input.RetirementAge;
                    var adjustedSpending = input.AnnualRetirementSpending *
                        (decimal)Math.Pow((double)(1 + input.InflationRate), inflationYears);

                    var remaining = WithdrawFromAccounts(balances, input.AccountAssumptions, adjustedSpending, WithdrawOrder.TaxableFirst);
                    if (remaining > 0)
                        depleted = true;
                }

                allRuns[iter][year] = balances.Values.Sum();
            }

            if (!depleted)
                successCount++;
        }

        var bands = new List<PercentileBand>();
        int[] percentiles = [10, 25, 50, 75, 90];

        for (int year = 0; year <= yearsToProject; year++)
        {
            var values = new decimal[iterations];
            for (int iter = 0; iter < iterations; iter++)
                values[iter] = allRuns[iter][year];

            Array.Sort(values);

            var band = new PercentileBand
            {
                Year = input.CurrentYear + year,
                Age = currentAge + year
            };

            foreach (var p in percentiles)
            {
                var idx = (int)Math.Round((p / 100.0) * (iterations - 1));
                band.Values[p] = Math.Round(values[idx], 2);
            }

            bands.Add(band);
        }

        return new MonteCarloResult
        {
            SuccessProbability = Math.Round((decimal)successCount / iterations * 100, 1),
            PercentileBands = bands
        };
    }

    public static FireResult ComputeFire(ProjectionInput input)
    {
        var currentAge = input.CurrentYear - input.BirthYear;
        var currentTotal = input.AccountAssumptions.Sum(a => a.CurrentBalance);
        var fireNumber = input.AnnualRetirementSpending * 25;
        var progressPercent = fireNumber > 0 ? Math.Round(currentTotal / fireNumber * 100, 1) : 0;

        int? projectedFireAge = null;
        if (currentTotal < fireNumber)
        {
            var balance = currentTotal;
            for (int year = 1; year <= 100; year++)
            {
                var age = currentAge + year;
                foreach (var a in input.AccountAssumptions)
                {
                    var contribution = age < a.ContributionEndAge ? a.AnnualContribution + a.EmployerMatch : 0;
                    balance += balance * a.ExpectedReturnRate / input.AccountAssumptions.Count + contribution;
                }
                // Simplified: use weighted average growth
                if (balance >= fireNumber)
                {
                    projectedFireAge = age;
                    break;
                }
            }
        }
        else
        {
            projectedFireAge = currentAge;
        }

        // More accurate: simulate aggregate portfolio growth
        projectedFireAge = ComputeFireAgeAccurate(input, currentAge, fireNumber);

        return new FireResult
        {
            FireNumber = fireNumber,
            CurrentTotal = currentTotal,
            ProgressPercent = progressPercent,
            ProjectedFireAge = projectedFireAge
        };
    }

    private static int? ComputeFireAgeAccurate(ProjectionInput input, int currentAge, decimal fireNumber)
    {
        var balances = new Dictionary<int, decimal>();
        foreach (var a in input.AccountAssumptions)
            balances[a.AccountId] = a.CurrentBalance;

        if (balances.Values.Sum() >= fireNumber)
            return currentAge;

        for (int year = 1; year <= 100; year++)
        {
            var age = currentAge + year;
            foreach (var a in input.AccountAssumptions)
            {
                var balance = balances[a.AccountId];
                balance *= (1 + a.ExpectedReturnRate);
                if (age < a.ContributionEndAge)
                    balance += a.AnnualContribution + a.EmployerMatch;
                balances[a.AccountId] = balance;
            }

            if (balances.Values.Sum() >= fireNumber)
                return age;
        }

        return null;
    }

    private static void ApplyLifeEvents(Dictionary<int, decimal> balances, ProjectionInput input, int age)
    {
        if (input.LifeEvents.Count == 0) return;

        foreach (var evt in input.LifeEvents)
        {
            bool applies = evt.IsRecurring
                ? age >= evt.Age && (!evt.EndAge.HasValue || age <= evt.EndAge.Value)
                : age == evt.Age;

            if (!applies) continue;

            if (evt.Amount >= 0)
            {
                // Positive: distribute proportionally across accounts
                var total = balances.Values.Sum();
                if (total > 0)
                {
                    foreach (var key in balances.Keys.ToList())
                        balances[key] += evt.Amount * (balances[key] / total);
                }
                else if (balances.Count > 0)
                {
                    var firstKey = balances.Keys.First();
                    balances[firstKey] += evt.Amount;
                }
            }
            else
            {
                // Negative: withdraw using standard order
                WithdrawFromAccounts(balances, input.AccountAssumptions, Math.Abs(evt.Amount), WithdrawOrder.TaxableFirst);
            }
        }
    }

    private static decimal GetAccountStdDev(AccountType type, decimal defaultStdDev) => type switch
    {
        AccountType.Checking or AccountType.Savings => 0.02m,
        AccountType.CreditCard => 0,
        _ => defaultStdDev
    };

    private static double SampleNormalReturn(Random rng, double mean, double stdDev)
    {
        // Box-Muller transform
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + stdDev * z;
    }

    private static decimal WithdrawFromAccounts(
        Dictionary<int, decimal> balances,
        List<AccountAssumptionInput> assumptions,
        decimal amount,
        WithdrawOrder order)
    {
        var ordered = order switch
        {
            WithdrawOrder.TaxableFirst => assumptions
                .OrderBy(a => GetWithdrawPriority(a.AccountType))
                .ToList(),
            _ => assumptions.ToList()
        };

        var remaining = amount;
        foreach (var assumption in ordered)
        {
            if (remaining <= 0) break;

            var available = balances[assumption.AccountId];
            var withdrawal = Math.Min(available, remaining);
            balances[assumption.AccountId] -= withdrawal;
            remaining -= withdrawal;
        }

        return remaining;
    }

    private static int GetWithdrawPriority(AccountType type) => type switch
    {
        AccountType.Checking => 0,
        AccountType.Savings => 1,
        AccountType.Brokerage => 2,
        AccountType.Retirement401k => 3,
        AccountType.TraditionalIra => 4,
        AccountType.RothIra => 5,
        _ => 6
    };

    private enum WithdrawOrder { TaxableFirst }
}

public class ProjectionInput
{
    public int CurrentYear { get; set; }
    public int BirthYear { get; set; }
    public int RetirementAge { get; set; }
    public int LifeExpectancy { get; set; }
    public decimal AnnualRetirementSpending { get; set; }
    public decimal InflationRate { get; set; }
    public List<AccountAssumptionInput> AccountAssumptions { get; set; } = [];
    public List<LifeEventInput> LifeEvents { get; set; } = [];
}

public class AccountAssumptionInput
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = "";
    public AccountType AccountType { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal AnnualContribution { get; set; }
    public decimal EmployerMatch { get; set; }
    public decimal ExpectedReturnRate { get; set; }
    public int ContributionEndAge { get; set; }
}

public class LifeEventInput
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public decimal Amount { get; set; }
    public bool IsRecurring { get; set; }
    public int? EndAge { get; set; }
}

public class ProjectionResult
{
    public List<YearlySnapshot> YearlySnapshots { get; set; } = [];
    public decimal RetirementBalance { get; set; }
    public int? DepletionAge { get; set; }
    public decimal SurplusAtDeath { get; set; }
    public decimal SafeSpendingEstimate { get; set; }
}

public class YearlySnapshot
{
    public int Year { get; set; }
    public int Age { get; set; }
    public Dictionary<int, decimal> AccountBalances { get; set; } = new();
    public decimal TotalBalance { get; set; }
}

public class MonteCarloResult
{
    public decimal SuccessProbability { get; set; }
    public List<PercentileBand> PercentileBands { get; set; } = [];
}

public class PercentileBand
{
    public int Year { get; set; }
    public int Age { get; set; }
    public Dictionary<int, decimal> Values { get; set; } = new();
}

public class FireResult
{
    public decimal FireNumber { get; set; }
    public decimal CurrentTotal { get; set; }
    public decimal ProgressPercent { get; set; }
    public int? ProjectedFireAge { get; set; }
}
