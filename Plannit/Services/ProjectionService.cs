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
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<ProjectionScenario>> GetScenariosAsync()
    {
        return await _db.ProjectionScenarios
            .Include(s => s.AccountAssumptions)
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
        var scenario = await _db.ProjectionScenarios.FindAsync(id);
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
