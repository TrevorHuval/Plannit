using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Models.ViewModels;
using Plannit.Services;

namespace Plannit.Tests;

public class HoldingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "user-1";

    public HoldingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var seedDb = new ApplicationDbContext(_options);
        seedDb.Database.EnsureCreated();

        var user = new IdentityUser { Id = UserId, UserName = "user@test.com", Email = "user@test.com", NormalizedEmail = "USER@TEST.COM" };
        seedDb.Users.Add(user);
        seedDb.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext CreateContext()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    private async Task<int> CreateAccountAsync(AccountType type = AccountType.RothIra, string? name = null)
    {
        using var db = CreateContext();
        var account = new Account { UserId = UserId, Name = name ?? type.ToString(), Type = type };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static List<PositionLineViewModel> SamplePositions() => new()
    {
        new() { Symbol = "FXAIX", Description = "FIDELITY 500 INDEX FUND", Value = 21463.32m, Quantity = 81.529m, Price = 263.26m, CostBasis = 15002.63m },
        new() { Symbol = "MU", Description = "MICRON TECHNOLOGY INC", Value = 2771.41m, Quantity = 2.83m, Price = 979.30m, CostBasis = 249.95m },
        new() { Symbol = "SPAXX**", Description = "HELD IN MONEY MARKET", Value = 4.72m }
    };

    [Fact]
    public async Task UpsertHoldingsAsync_CreatesHoldingsAndSnapshots()
    {
        var accountId = await CreateAccountAsync();
        var date = new DateOnly(2026, 7, 12);

        using (var db = CreateContext())
        {
            var service = new HoldingService(db);
            var count = await service.UpsertHoldingsAsync(accountId, date, SamplePositions());
            Assert.Equal(3, count);
        }

        using (var db = CreateContext())
        {
            var holdings = await db.Holdings.Include(h => h.Snapshots).ToListAsync();
            Assert.Equal(3, holdings.Count);
            Assert.All(holdings, h => Assert.Single(h.Snapshots));

            var fxaix = holdings.Single(h => h.Symbol == "FXAIX");
            Assert.Equal(81.529m, fxaix.Quantity);
            Assert.Equal(15002.63m, fxaix.CostBasis);
            var snap = fxaix.Snapshots.Single();
            Assert.Equal(date, snap.Date);
            Assert.Equal(263.26m, snap.Price);
            Assert.Equal(21463.32m, snap.Value);

            // Cash line with no quantity/price carries only a value.
            var spaxx = holdings.Single(h => h.Symbol == "SPAXX**");
            Assert.Null(spaxx.Quantity);
            Assert.Equal(4.72m, spaxx.Snapshots.Single().Value);
        }
    }

    [Fact]
    public async Task UpsertHoldingsAsync_SameDateReimport_UpdatesInPlace()
    {
        var accountId = await CreateAccountAsync();
        var date = new DateOnly(2026, 7, 12);

        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, date, SamplePositions());

        // Re-import the same date with a changed FXAIX value; should overwrite, not duplicate.
        var revised = SamplePositions();
        revised[0].Value = 22000m;
        revised[0].Quantity = 82m;
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, date, revised);

        using (var db = CreateContext())
        {
            var fxaix = await db.Holdings.Include(h => h.Snapshots).SingleAsync(h => h.Symbol == "FXAIX");
            Assert.Single(fxaix.Snapshots);
            Assert.Equal(22000m, fxaix.Snapshots.Single().Value);
            Assert.Equal(82m, fxaix.Quantity);
            Assert.Equal(3, await db.Holdings.CountAsync());
        }
    }

    [Fact]
    public async Task UpsertHoldingsAsync_NewDate_AppendsHistoryToSameHolding()
    {
        var accountId = await CreateAccountAsync();

        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, new DateOnly(2026, 6, 12), SamplePositions());

        var later = SamplePositions();
        later[0].Value = 22500m;
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, new DateOnly(2026, 7, 12), later);

        using (var db = CreateContext())
        {
            var fxaix = await db.Holdings.Include(h => h.Snapshots).SingleAsync(h => h.Symbol == "FXAIX");
            Assert.Equal(2, fxaix.Snapshots.Count);
            Assert.Equal(3, await db.Holdings.CountAsync()); // still 3 holdings, not 6
        }
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_ComputesWeightsAndGainLoss()
    {
        var accountId = await CreateAccountAsync();
        var date = new DateOnly(2026, 7, 12);
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, date, SamplePositions());

        using var ctx = CreateContext();
        var vm = await new HoldingService(ctx).GetAccountHoldingsAsync(accountId);

        var total = 21463.32m + 2771.41m + 4.72m;
        Assert.Equal(total, vm.TotalValue);
        Assert.Equal(date, vm.AsOfDate);
        Assert.Equal(3, vm.Holdings.Count);

        // Ordered by value descending; weights sum to 1.
        Assert.Equal("FXAIX", vm.Holdings[0].Symbol);
        Assert.Equal(21463.32m / total, vm.Holdings[0].Weight);
        Assert.Equal(1m, vm.Holdings.Sum(h => h.Weight), 6);

        var fxaix = vm.Holdings.Single(h => h.Symbol == "FXAIX");
        Assert.Equal(21463.32m - 15002.63m, fxaix.GainLoss);

        // Cash line has no cost basis → no gain/loss.
        var spaxx = vm.Holdings.Single(h => h.Symbol == "SPAXX**");
        Assert.Null(spaxx.GainLoss);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_UsesLatestSnapshotValue()
    {
        var accountId = await CreateAccountAsync();
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, new DateOnly(2026, 6, 12), SamplePositions());

        var later = SamplePositions();
        later[0].Value = 25000m;
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(accountId, new DateOnly(2026, 7, 12), later);

        using var ctx = CreateContext();
        var vm = await new HoldingService(ctx).GetAccountHoldingsAsync(accountId);

        Assert.Equal(new DateOnly(2026, 7, 12), vm.AsOfDate);
        Assert.Equal(25000m, vm.Holdings.Single(h => h.Symbol == "FXAIX").Value);
    }

    [Fact]
    public async Task GetPortfolioAsync_AggregatesBySymbolAndFlagsConcentration()
    {
        var rothId = await CreateAccountAsync(AccountType.RothIra);
        var brokerageId = await CreateAccountAsync(AccountType.Brokerage);
        var date = new DateOnly(2026, 7, 12);

        using (var db = CreateContext())
        {
            var service = new HoldingService(db);
            await service.UpsertHoldingsAsync(rothId, date, new List<PositionLineViewModel>
            {
                new() { Symbol = "FXAIX", Description = "FIDELITY 500 INDEX", Value = 6000m, CostBasis = 4000m }
            });
            await service.UpsertHoldingsAsync(brokerageId, date, new List<PositionLineViewModel>
            {
                new() { Symbol = "FXAIX", Description = "FIDELITY 500 INDEX", Value = 2000m, CostBasis = 1500m },
                new() { Symbol = "VTI", Description = "VANGUARD TOTAL", Value = 2000m, CostBasis = 2200m }
            });
        }

        using var ctx = CreateContext();
        var vm = await new HoldingService(ctx).GetPortfolioAsync();

        Assert.Equal(10000m, vm.TotalValue);

        var fxaix = vm.Positions.Single(p => p.Symbol == "FXAIX");
        Assert.Equal(8000m, fxaix.Value);               // aggregated across both accounts
        Assert.Equal(5500m, fxaix.CostBasis);
        Assert.Equal(0.80m, fxaix.Weight);
        Assert.Equal(2500m, fxaix.GainLoss);
        Assert.Equal(2, fxaix.Accounts.Count);
        Assert.True(fxaix.IsConcentrated);              // 80% > 20%

        var vti = vm.Positions.Single(p => p.Symbol == "VTI");
        Assert.Equal(0.20m, vti.Weight);
        Assert.False(vti.IsConcentrated);               // exactly 20% is not "over"
        Assert.Single(vm.ConcentrationWarnings);
    }

    [Fact]
    public async Task GetPortfolioAsync_ExcludesNonInvestmentAccounts()
    {
        var checkingId = await CreateAccountAsync(AccountType.Checking);
        var date = new DateOnly(2026, 7, 12);

        // Even if a checking account somehow had a holding row, the portfolio ignores it.
        using (var db = CreateContext())
            await new HoldingService(db).UpsertHoldingsAsync(checkingId, date, new List<PositionLineViewModel>
            {
                new() { Symbol = "FXAIX", Value = 1000m }
            });

        using var ctx = CreateContext();
        var vm = await new HoldingService(ctx).GetPortfolioAsync();

        Assert.False(vm.HasHoldings);
    }
}
