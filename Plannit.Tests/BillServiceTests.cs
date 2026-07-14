using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class BillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "bill-user";
    private int _accountId;

    public BillServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var db = new ApplicationDbContext(_options);
        db.Database.EnsureCreated();
        db.Users.Add(new IdentityUser { Id = UserId, UserName = "bills@test.com", Email = "bills@test.com", NormalizedEmail = "BILLS@TEST.COM" });
        db.SaveChanges();

        var account = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.Add(account);
        db.SaveChanges();
        _accountId = account.Id;
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext CreateContext()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    // ===== Occurrence projection =====

    [Fact]
    public void ProjectOccurrences_Monthly_ReturnsOneOccurrencePerMonth()
    {
        var anchor = new DateOnly(2026, 1, 15);
        var occurrences = BillService.ProjectOccurrences(anchor, RecurringCadence.Monthly, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30));

        Assert.Equal(
            new[] { new DateOnly(2026, 1, 15), new DateOnly(2026, 2, 15), new DateOnly(2026, 3, 15), new DateOnly(2026, 4, 15) },
            occurrences);
    }

    [Fact]
    public void ProjectOccurrences_StaleAnchorBeforeRange_FastForwardsIntoRange()
    {
        // NextDue is two months overdue relative to the visible window — projection should
        // still surface the in-range occurrences instead of nothing.
        var anchor = new DateOnly(2026, 1, 1);
        var occurrences = BillService.ProjectOccurrences(anchor, RecurringCadence.Monthly, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        Assert.Equal([new DateOnly(2026, 3, 1)], occurrences);
    }

    [Fact]
    public void ProjectOccurrences_AnchorAfterRange_ReturnsEmpty()
    {
        var anchor = new DateOnly(2026, 6, 1);
        var occurrences = BillService.ProjectOccurrences(anchor, RecurringCadence.Monthly, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

        Assert.Empty(occurrences);
    }

    // ===== Reconciliation =====

    [Fact]
    public async Task ReconcileAsync_MatchesTransaction_AndAdvancesNextDue()
    {
        using (var seedDb = CreateContext())
        {
            seedDb.Bills.Add(new Bill
            {
                UserId = UserId,
                MerchantKey = "NETFLIX",
                Name = "Netflix",
                Cadence = RecurringCadence.Monthly,
                ExpectedAmount = 15.99m,
                NextDue = new DateOnly(2026, 2, 1),
                IsIncome = false,
                Source = BillSource.Manual
            });
            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = new DateOnly(2026, 2, 2),
                Amount = -15.99m,
                Description = "Netflix"
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = new BillService(db);
        var matched = await service.ReconcileAsync();

        Assert.Equal(1, matched);
        var bill = await db.Bills.AsNoTracking().FirstAsync();
        Assert.Equal(new DateOnly(2026, 2, 2), bill.LastPaidDate);
        Assert.Equal(new DateOnly(2026, 3, 2), bill.NextDue);
    }

    [Fact]
    public async Task ReconcileAsync_CatchesUpMultipleMissedOccurrences()
    {
        using (var seedDb = CreateContext())
        {
            seedDb.Bills.Add(new Bill
            {
                UserId = UserId,
                MerchantKey = "NETFLIX",
                Name = "Netflix",
                Cadence = RecurringCadence.Monthly,
                ExpectedAmount = 15.99m,
                NextDue = new DateOnly(2026, 1, 1),
                IsIncome = false,
                Source = BillSource.Manual
            });
            seedDb.Transactions.AddRange(
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -15.99m, Description = "Netflix" },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 2, 1), Amount = -15.99m, Description = "Netflix" },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 3, 1), Amount = -15.99m, Description = "Netflix" }
            );
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = new BillService(db);
        var matched = await service.ReconcileAsync();

        Assert.Equal(3, matched);
        var bill = await db.Bills.AsNoTracking().FirstAsync();
        Assert.Equal(new DateOnly(2026, 4, 1), bill.NextDue);
    }

    [Fact]
    public async Task ReconcileAsync_IgnoresUnrelatedMerchant()
    {
        using (var seedDb = CreateContext())
        {
            seedDb.Bills.Add(new Bill
            {
                UserId = UserId,
                MerchantKey = "NETFLIX",
                Name = "Netflix",
                Cadence = RecurringCadence.Monthly,
                ExpectedAmount = 15.99m,
                NextDue = new DateOnly(2026, 2, 1),
                IsIncome = false,
                Source = BillSource.Manual
            });
            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = new DateOnly(2026, 2, 2),
                Amount = -15.99m,
                Description = "Whole Foods Market"
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = new BillService(db);
        var matched = await service.ReconcileAsync();

        Assert.Equal(0, matched);
        var bill = await db.Bills.AsNoTracking().FirstAsync();
        Assert.Equal(new DateOnly(2026, 2, 1), bill.NextDue);
        Assert.Null(bill.LastPaidDate);
    }

    // ===== Promote idempotency =====

    [Fact]
    public async Task PromoteAsync_CalledTwice_DoesNotCreateDuplicate()
    {
        using var db = CreateContext();
        var service = new BillService(db);

        await service.PromoteAsync(UserId, "Spotify", RecurringCadence.Monthly, 9.99m, new DateOnly(2026, 2, 1), false);
        await service.PromoteAsync(UserId, "Spotify", RecurringCadence.Monthly, 9.99m, new DateOnly(2026, 2, 1), false);

        var bills = await db.Bills.AsNoTracking().ToListAsync();
        Assert.Single(bills);
    }
}
