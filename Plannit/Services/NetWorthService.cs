using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class NetWorthService
{
    internal static readonly HashSet<AccountType> LiabilityTypes = [AccountType.CreditCard, AccountType.Loan, AccountType.Mortgage];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public NetWorthService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private string CacheKey(string suffix) => $"networth:{_db.CurrentUserId}:{_db.CacheVersion}:{suffix}";

    public async Task<decimal> GetCurrentNetWorthAsync()
    {
        var key = CacheKey("current");
        if (_cache.TryGetValue(key, out decimal cached)) return cached;

        var value = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new
            {
                a.Type,
                LatestBalance = a.Snapshots.OrderByDescending(s => s.Date).Select(s => (decimal?)s.Balance).FirstOrDefault()
            })
            .Where(a => a.LatestBalance != null)
            .SumAsync(a => LiabilityTypes.Contains(a.Type) ? -a.LatestBalance!.Value : a.LatestBalance!.Value);

        _cache.Set(key, value, CacheTtl);
        return value;
    }

    public async Task<Dictionary<AccountType, decimal>> GetTotalsByTypeAsync()
    {
        var key = CacheKey("totalsByType");
        if (_cache.TryGetValue(key, out Dictionary<AccountType, decimal>? cached) && cached is not null)
            return cached;

        var totals = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new
            {
                a.Type,
                LatestBalance = a.Snapshots.OrderByDescending(s => s.Date).Select(s => (decimal?)s.Balance).FirstOrDefault()
            })
            .Where(a => a.LatestBalance != null)
            .GroupBy(a => a.Type)
            .Select(g => new { Type = g.Key, Total = g.Sum(a => a.LatestBalance!.Value) })
            .ToListAsync();

        var result = totals.ToDictionary(t => t.Type, t => t.Total);
        _cache.Set(key, result, CacheTtl);
        return result;
    }

    // A single-pass merge over all snapshots (ordered by date) reproduces "latest snapshot at
    // or before each date, per account" without the O(dates * accounts) re-scan the naive
    // version did: each account has at most one snapshot per date (unique index), so walking
    // dates in order and overwriting a running per-account balance is equivalent to redoing
    // the at-or-before lookup for every date.
    public async Task<List<(DateOnly Date, decimal NetWorth)>> GetNetWorthHistoryAsync()
    {
        var key = CacheKey("history");
        if (_cache.TryGetValue(key, out List<(DateOnly Date, decimal NetWorth)>? cached) && cached is not null)
            return cached;

        var snapshots = await _db.BalanceSnapshots
            .AsNoTracking()
            .Where(s => s.Account.IsActive)
            .Select(s => new { s.Date, s.Balance, s.AccountId, s.Account.Type })
            .OrderBy(s => s.Date)
            .ToListAsync();

        var history = new List<(DateOnly Date, decimal NetWorth)>();
        var runningBalances = new Dictionary<int, decimal>();
        var runningTotal = 0m;

        var i = 0;
        while (i < snapshots.Count)
        {
            var date = snapshots[i].Date;
            while (i < snapshots.Count && snapshots[i].Date == date)
            {
                var s = snapshots[i];
                var signed = LiabilityTypes.Contains(s.Type) ? -s.Balance : s.Balance;
                if (runningBalances.TryGetValue(s.AccountId, out var old))
                    runningTotal -= old;
                runningBalances[s.AccountId] = signed;
                runningTotal += signed;
                i++;
            }
            history.Add((date, runningTotal));
        }

        _cache.Set(key, history, CacheTtl);
        return history;
    }

    public async Task<decimal> GetNetWorthAtDateAsync(DateOnly asOf)
    {
        var key = CacheKey($"atDate:{asOf:yyyy-MM-dd}");
        if (_cache.TryGetValue(key, out decimal cached)) return cached;

        var value = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new
            {
                a.Type,
                Balance = a.Snapshots.Where(s => s.Date <= asOf).OrderByDescending(s => s.Date).Select(s => (decimal?)s.Balance).FirstOrDefault()
            })
            .Where(a => a.Balance != null)
            .SumAsync(a => LiabilityTypes.Contains(a.Type) ? -a.Balance!.Value : a.Balance!.Value);

        _cache.Set(key, value, CacheTtl);
        return value;
    }

    public async Task<List<StaleAccountInfo>> GetStaleAccountsAsync(int thresholdDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var accounts = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.CreatedAt,
                LatestDate = a.Snapshots.OrderByDescending(s => s.Date).Select(s => (DateOnly?)s.Date).FirstOrDefault()
            })
            .ToListAsync();

        var stale = new List<StaleAccountInfo>();
        foreach (var account in accounts)
        {
            if (account.LatestDate is null)
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
                var days = today.DayNumber - account.LatestDate.Value.DayNumber;
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
        AccountType.Loan => "Loan",
        AccountType.Mortgage => "Mortgage",
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
