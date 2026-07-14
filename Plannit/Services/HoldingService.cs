using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;

namespace Plannit.Services;

public class HoldingService
{
    // Account types whose value is composed of individual security positions.
    internal static readonly HashSet<AccountType> InvestmentTypes =
        [AccountType.Retirement401k, AccountType.RothIra, AccountType.TraditionalIra, AccountType.Brokerage];

    public static bool IsInvestment(AccountType type) => InvestmentTypes.Contains(type);

    private readonly ApplicationDbContext _db;

    public HoldingService(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Upserts holdings and a dated holding snapshot for an account from a parsed positions
    /// export. One <see cref="Holding"/> per account+symbol carries the latest quantity/cost
    /// basis; each import adds/updates a <see cref="HoldingSnapshot"/> for that as-of date.
    /// </summary>
    public async Task<int> UpsertHoldingsAsync(int accountId, DateOnly date, IEnumerable<PositionLineViewModel> positions)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null) return 0;

        var count = 0;
        foreach (var pos in positions)
        {
            if (string.IsNullOrWhiteSpace(pos.Symbol)) continue;

            var holding = await _db.Holdings
                .Include(h => h.Snapshots)
                .FirstOrDefaultAsync(h => h.AccountId == accountId && h.Symbol == pos.Symbol);

            if (holding is null)
            {
                holding = new Holding
                {
                    AccountId = accountId,
                    Symbol = pos.Symbol,
                    Description = pos.Description,
                    Quantity = pos.Quantity,
                    CostBasis = pos.CostBasis
                };
                _db.Holdings.Add(holding);
            }
            else
            {
                holding.Description = pos.Description;
                holding.Quantity = pos.Quantity;
                holding.CostBasis = pos.CostBasis;
            }

            var existing = holding.Snapshots.FirstOrDefault(s => s.Date == date);
            if (existing is not null)
            {
                existing.Quantity = pos.Quantity;
                existing.Price = pos.Price;
                existing.Value = pos.Value;
            }
            else
            {
                holding.Snapshots.Add(new HoldingSnapshot
                {
                    Date = date,
                    Quantity = pos.Quantity,
                    Price = pos.Price,
                    Value = pos.Value
                });
            }

            count++;
        }

        await _db.SaveChangesAsync();
        return count;
    }

    /// <summary>Holdings for one account, valued at each holding's most recent snapshot,
    /// with weight (% of account total) and gain/loss vs. cost basis where known.</summary>
    public async Task<AccountHoldingsViewModel> GetAccountHoldingsAsync(int accountId)
    {
        var holdings = await _db.Holdings
            .AsNoTracking()
            .Where(h => h.AccountId == accountId)
            .Include(h => h.Snapshots)
            .ToListAsync();

        var vm = new AccountHoldingsViewModel();
        if (holdings.Count == 0) return vm;

        var rows = new List<HoldingViewModel>();
        DateOnly? asOf = null;
        foreach (var h in holdings)
        {
            var latest = h.Snapshots.MaxBy(s => s.Date);
            if (latest is null) continue;

            if (asOf is null || latest.Date > asOf) asOf = latest.Date;

            rows.Add(new HoldingViewModel
            {
                Id = h.Id,
                Symbol = h.Symbol,
                Description = h.Description,
                Quantity = latest.Quantity ?? h.Quantity,
                Price = latest.Price,
                Value = latest.Value,
                CostBasis = h.CostBasis
            });
        }

        var total = rows.Sum(r => r.Value);
        foreach (var r in rows)
            r.Weight = total != 0 ? r.Value / total : 0;

        vm.TotalValue = total;
        vm.AsOfDate = asOf;
        vm.Holdings = rows.OrderByDescending(r => r.Value).ToList();

        vm.History = holdings
            .Select(h => new HoldingHistorySeries
            {
                Symbol = h.Symbol,
                Points = h.Snapshots
                    .OrderBy(s => s.Date)
                    .Select(s => new HoldingHistoryPoint { Date = s.Date, Value = s.Value })
                    .ToList()
            })
            .Where(s => s.Points.Count > 0)
            .OrderBy(s => s.Symbol)
            .ToList();

        return vm;
    }

    /// <summary>
    /// Portfolio-wide allocation: each investment account's holdings valued at their latest
    /// snapshot, aggregated by symbol across accounts, with concentration flags (&gt;20%).
    /// </summary>
    public async Task<PortfolioViewModel> GetPortfolioAsync()
    {
        var holdings = await _db.Holdings
            .AsNoTracking()
            .Where(h => h.Account.IsActive && InvestmentTypes.Contains(h.Account.Type))
            .Select(h => new
            {
                h.Symbol,
                h.Description,
                h.CostBasis,
                AccountName = h.Account.Name,
                Latest = h.Snapshots.OrderByDescending(s => s.Date)
                    .Select(s => (decimal?)s.Value).FirstOrDefault()
            })
            .Where(h => h.Latest != null)
            .ToListAsync();

        var vm = new PortfolioViewModel();
        if (holdings.Count == 0) return vm;

        var positions = holdings
            .GroupBy(h => h.Symbol)
            .Select(g => new PortfolioPositionViewModel
            {
                Symbol = g.Key,
                Description = g.Select(x => x.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                Value = g.Sum(x => x.Latest!.Value),
                CostBasis = g.Any(x => x.CostBasis.HasValue) ? g.Sum(x => x.CostBasis ?? 0) : null,
                Accounts = g.Select(x => x.AccountName).Distinct().OrderBy(n => n).ToList()
            })
            .ToList();

        var total = positions.Sum(p => p.Value);
        foreach (var p in positions)
            p.Weight = total != 0 ? p.Value / total : 0;

        vm.TotalValue = total;
        vm.Positions = positions.OrderByDescending(p => p.Value).ToList();
        return vm;
    }
}
