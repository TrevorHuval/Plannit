using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class NetWorthService
{
    private static readonly HashSet<AccountType> LiabilityTypes = [AccountType.CreditCard];

    private readonly ApplicationDbContext _db;

    public NetWorthService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<decimal> GetCurrentNetWorthAsync()
    {
        var accounts = await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .ToListAsync();

        return accounts.Sum(a =>
        {
            var latest = a.Snapshots.MaxBy(s => s.Date);
            if (latest is null) return 0m;
            return AccountConventions.SignedBalance(a.Type, latest.Balance);
        });
    }

    public async Task<Dictionary<AccountType, decimal>> GetTotalsByTypeAsync()
    {
        var accounts = await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .ToListAsync();

        var result = new Dictionary<AccountType, decimal>();
        foreach (var account in accounts)
        {
            var latest = account.Snapshots.MaxBy(s => s.Date);
            if (latest is null) continue;

            var type = account.Type;
            result.TryGetValue(type, out var current);
            result[type] = current + latest.Balance;
        }
        return result;
    }

    public async Task<List<(DateOnly Date, decimal NetWorth)>> GetNetWorthHistoryAsync()
    {
        var accounts = await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .ToListAsync();

        var allDates = accounts
            .SelectMany(a => a.Snapshots.Select(s => s.Date))
            .Distinct()
            .Order()
            .ToList();

        var history = new List<(DateOnly Date, decimal NetWorth)>();

        foreach (var date in allDates)
        {
            decimal netWorth = 0;
            foreach (var account in accounts)
            {
                var snapshotAtOrBefore = account.Snapshots
                    .Where(s => s.Date <= date)
                    .MaxBy(s => s.Date);

                if (snapshotAtOrBefore is null) continue;

                netWorth += AccountConventions.SignedBalance(account.Type, snapshotAtOrBefore.Balance);
            }
            history.Add((date, netWorth));
        }

        return history;
    }

    public async Task<decimal> GetNetWorthAtDateAsync(DateOnly asOf)
    {
        var accounts = await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .ToListAsync();

        decimal netWorth = 0;
        foreach (var account in accounts)
        {
            var snapshotAtOrBefore = account.Snapshots
                .Where(s => s.Date <= asOf)
                .MaxBy(s => s.Date);

            if (snapshotAtOrBefore is null) continue;

            netWorth += AccountConventions.SignedBalance(account.Type, snapshotAtOrBefore.Balance);
        }
        return netWorth;
    }

    public async Task<List<StaleAccountInfo>> GetStaleAccountsAsync(int thresholdDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var accounts = await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .ToListAsync();

        var stale = new List<StaleAccountInfo>();
        foreach (var account in accounts)
        {
            var latest = account.Snapshots.MaxBy(s => s.Date);
            if (latest is null)
            {
                stale.Add(new StaleAccountInfo
                {
                    AccountId = account.Id,
                    AccountName = account.Name,
                    DaysSinceUpdate = (today.DayNumber - DateOnly.FromDateTime(account.CreatedAt).DayNumber)
                });
            }
            else
            {
                var days = today.DayNumber - latest.Date.DayNumber;
                if (days >= thresholdDays)
                {
                    stale.Add(new StaleAccountInfo
                    {
                        AccountId = account.Id,
                        AccountName = account.Name,
                        DaysSinceUpdate = days
                    });
                }
            }
        }
        return stale.OrderByDescending(s => s.DaysSinceUpdate).ToList();
    }

    public static bool IsLiability(AccountType type) => LiabilityTypes.Contains(type);

    public static string FormatAccountType(AccountType type) => type switch
    {
        AccountType.Checking => "Checking",
        AccountType.Savings => "Savings",
        AccountType.CreditCard => "Credit Card",
        AccountType.Retirement401k => "401(k)",
        AccountType.RothIra => "Roth IRA",
        AccountType.TraditionalIra => "Traditional IRA",
        AccountType.Brokerage => "Brokerage",
        AccountType.Other => "Other",
        _ => type.ToString()
    };
}

public class StaleAccountInfo
{
    public int AccountId { get; set; }
    public string AccountName { get; set; } = null!;
    public int DaysSinceUpdate { get; set; }
}
