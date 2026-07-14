using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

/// <summary>Amortization and multi-debt payoff-strategy math for Loan/Mortgage accounts.
/// The simulation methods are pure and DB-free so they're directly unit-testable.</summary>
public class LoanService
{
    private static readonly AccountType[] DebtTypes = [AccountType.Loan, AccountType.Mortgage];

    private readonly ApplicationDbContext _db;

    public LoanService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Account>> GetDebtAccountsAsync()
    {
        return await _db.Accounts
            .AsNoTracking()
            .Include(a => a.Snapshots)
            .Where(a => a.IsActive && DebtTypes.Contains(a.Type))
            .OrderBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<Account?> GetByIdAsync(int id)
    {
        return await _db.Accounts
            .AsNoTracking()
            .Include(a => a.Snapshots)
            .FirstOrDefaultAsync(a => a.Id == id && DebtTypes.Contains(a.Type));
    }

    /// <summary>Standard fixed-payment amortization: each period, interest accrues on the
    /// remaining balance, and the rest of the payment reduces principal. Stops once the
    /// balance reaches zero or <paramref name="maxMonths"/> is hit (a payment that doesn't
    /// cover interest never finishes — bounded so it can't loop forever).</summary>
    public static List<AmortizationRow> RunAmortizationSchedule(decimal principal, decimal annualRate, decimal monthlyPayment, int maxMonths = 600)
    {
        var rows = new List<AmortizationRow>();
        var balance = principal;
        var monthlyRate = annualRate / 12m;

        for (var month = 1; month <= maxMonths && balance > 0.005m; month++)
        {
            var interest = Math.Round(balance * monthlyRate, 2);
            var principalPortion = monthlyPayment - interest;
            if (principalPortion <= 0) break;
            if (principalPortion > balance) principalPortion = balance;

            balance -= principalPortion;
            rows.Add(new AmortizationRow
            {
                Month = month,
                Payment = principalPortion + interest,
                Principal = principalPortion,
                Interest = interest,
                RemainingBalance = balance
            });
        }

        return rows;
    }

    public static DebtPayoffComparison CompareStrategies(List<DebtAccountInput> debts, decimal extraPayment, DateOnly startDate)
    {
        return new DebtPayoffComparison
        {
            Avalanche = SimulatePayoff(debts, extraPayment, DebtOrderStrategy.Avalanche, startDate),
            Snowball = SimulatePayoff(debts, extraPayment, DebtOrderStrategy.Snowball, startDate)
        };
    }

    /// <summary>Simulates paying off every debt in parallel with a fixed total monthly budget
    /// (sum of minimum payments plus the extra payment). Priority order is fixed at the start
    /// — avalanche orders by highest interest rate first, snowball by smallest balance first.
    /// Each month, minimums are paid on every still-open debt, then any leftover budget
    /// (including minimums freed up by debts already paid off, since the loop simply skips
    /// them) goes entirely to the highest-priority open debt.</summary>
    public static DebtPayoffResult SimulatePayoff(List<DebtAccountInput> debts, decimal extraPayment, DebtOrderStrategy strategy, DateOnly startDate, int maxMonths = 600)
    {
        var order = (strategy == DebtOrderStrategy.Avalanche
                ? debts.OrderByDescending(d => d.AnnualRate).ThenBy(d => d.Balance)
                : debts.OrderBy(d => d.Balance).ThenByDescending(d => d.AnnualRate))
            .ToList();

        var balances = order.ToDictionary(d => d.AccountId, d => d.Balance);
        var interestPaid = order.ToDictionary(d => d.AccountId, _ => 0m);
        var payoffMonth = new Dictionary<int, int>();
        var totalBudget = debts.Sum(d => d.MinimumPayment) + extraPayment;

        var totalInterest = 0m;
        var month = 0;

        while (balances.Values.Any(b => b > 0.005m) && month < maxMonths)
        {
            month++;

            foreach (var d in order)
            {
                if (balances[d.AccountId] <= 0) continue;
                var interest = balances[d.AccountId] * d.AnnualRate / 12m;
                balances[d.AccountId] += interest;
                interestPaid[d.AccountId] += interest;
                totalInterest += interest;
            }

            var pool = totalBudget;

            foreach (var d in order)
            {
                if (balances[d.AccountId] <= 0) continue;
                var pay = Math.Min(d.MinimumPayment, balances[d.AccountId]);
                balances[d.AccountId] -= pay;
                pool -= pay;
            }

            foreach (var d in order)
            {
                if (pool <= 0) break;
                if (balances[d.AccountId] <= 0) continue;
                var pay = Math.Min(pool, balances[d.AccountId]);
                balances[d.AccountId] -= pay;
                pool -= pay;
            }

            foreach (var d in order)
            {
                if (balances[d.AccountId] <= 0.005m && !payoffMonth.ContainsKey(d.AccountId))
                    payoffMonth[d.AccountId] = month;
            }
        }

        var accountResults = order.Select(d => new DebtPayoffAccountResult
        {
            AccountId = d.AccountId,
            Name = d.Name,
            MonthsToPayoff = payoffMonth.GetValueOrDefault(d.AccountId, month),
            InterestPaid = Math.Round(interestPaid[d.AccountId], 2),
            PayoffDate = startDate.AddMonths(payoffMonth.GetValueOrDefault(d.AccountId, month))
        }).ToList();

        return new DebtPayoffResult
        {
            Accounts = accountResults,
            TotalMonths = month,
            TotalInterestPaid = Math.Round(totalInterest, 2),
            PayoffDate = startDate.AddMonths(month)
        };
    }
}

public class AmortizationRow
{
    public int Month { get; set; }
    public decimal Payment { get; set; }
    public decimal Principal { get; set; }
    public decimal Interest { get; set; }
    public decimal RemainingBalance { get; set; }
}

public class DebtAccountInput
{
    public int AccountId { get; set; }
    public string Name { get; set; } = null!;
    public decimal Balance { get; set; }
    public decimal AnnualRate { get; set; }
    public decimal MinimumPayment { get; set; }
}

public enum DebtOrderStrategy { Avalanche, Snowball }

public class DebtPayoffAccountResult
{
    public int AccountId { get; set; }
    public string Name { get; set; } = null!;
    public int MonthsToPayoff { get; set; }
    public decimal InterestPaid { get; set; }
    public DateOnly PayoffDate { get; set; }
}

public class DebtPayoffResult
{
    public List<DebtPayoffAccountResult> Accounts { get; set; } = new();
    public int TotalMonths { get; set; }
    public decimal TotalInterestPaid { get; set; }
    public DateOnly PayoffDate { get; set; }
}

public class DebtPayoffComparison
{
    public DebtPayoffResult Avalanche { get; set; } = null!;
    public DebtPayoffResult Snowball { get; set; } = null!;

    public decimal InterestSaved => Math.Max(Snowball.TotalInterestPaid - Avalanche.TotalInterestPaid, 0m);
    public int MonthsSaved => Math.Max(Snowball.TotalMonths - Avalanche.TotalMonths, 0);
}
