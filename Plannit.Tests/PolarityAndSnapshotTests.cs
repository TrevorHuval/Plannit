using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class PolarityAndSnapshotTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "user-1";

    public PolarityAndSnapshotTests()
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

    [Fact]
    public async Task AddSnapshotAsync_NormalizesNegativeLiabilityBalanceToPositive()
    {
        using var db = CreateContext();
        var account = new Account { UserId = UserId, Name = "Card", Type = AccountType.CreditCard };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var service = new AccountService(db);
        var snapshot = await service.AddSnapshotAsync(account.Id, new DateOnly(2026, 7, 12), -110.46m);

        Assert.NotNull(snapshot);
        Assert.Equal(110.46m, snapshot!.Balance);
    }

    [Fact]
    public async Task AddSnapshotAsync_LeavesAssetBalanceSignUnchanged()
    {
        using var db = CreateContext();
        var account = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var service = new AccountService(db);
        var snapshot = await service.AddSnapshotAsync(account.Id, new DateOnly(2026, 7, 12), 2500m);

        Assert.NotNull(snapshot);
        Assert.Equal(2500m, snapshot!.Balance);
    }

    [Fact]
    public async Task RepairLiabilitySnapshotSignsAsync_FixesOnlyNegativeLiabilitySnapshots()
    {
        using var db = CreateContext();
        var card = new Account { UserId = UserId, Name = "Card", Type = AccountType.CreditCard };
        var checking = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.AddRange(card, checking);
        await db.SaveChangesAsync();

        db.BalanceSnapshots.AddRange(
            new BalanceSnapshot { AccountId = card.Id, Date = new DateOnly(2026, 6, 1), Balance = -110.46m },
            new BalanceSnapshot { AccountId = checking.Id, Date = new DateOnly(2026, 6, 1), Balance = -50m }
        );
        await db.SaveChangesAsync();

        var service = new AccountService(db);
        var repaired = await service.RepairLiabilitySnapshotSignsAsync();

        Assert.Equal(1, repaired);

        var cardSnapshot = await db.BalanceSnapshots.FirstAsync(s => s.AccountId == card.Id);
        var checkingSnapshot = await db.BalanceSnapshots.FirstAsync(s => s.AccountId == checking.Id);
        Assert.Equal(110.46m, cardSnapshot.Balance);
        Assert.Equal(-50m, checkingSnapshot.Balance);
    }

    [Fact]
    public async Task RepairLiabilitySnapshotSignsAsync_IsIdempotent()
    {
        using var db = CreateContext();
        var card = new Account { UserId = UserId, Name = "Card", Type = AccountType.CreditCard };
        db.Accounts.Add(card);
        await db.SaveChangesAsync();
        db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = card.Id, Date = new DateOnly(2026, 6, 1), Balance = -110.46m });
        await db.SaveChangesAsync();

        var service = new AccountService(db);
        var firstPass = await service.RepairLiabilitySnapshotSignsAsync();
        var secondPass = await service.RepairLiabilitySnapshotSignsAsync();

        Assert.Equal(1, firstPass);
        Assert.Equal(0, secondPass);
    }

    [Fact]
    public async Task CsvImportAsync_InvertAmounts_FlipsSignAndHashReflectsInvertedAmount()
    {
        using var db = CreateContext();
        var card = new Account { UserId = UserId, Name = "Discover", Type = AccountType.CreditCard };
        db.Accounts.Add(card);
        await db.SaveChangesAsync();

        var csv = "Trans. Date,Post Date,Description,Amount,Category\n07/01/2026,07/02/2026,Whole Foods,7.95,Groceries\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var service = new CsvImportService(db);
        var result = await service.ImportAsync(stream, card.Id, "discover.csv",
            "Trans. Date", "MM/dd/yyyy", "Amount", null, null, "Description", invertAmounts: true);

        Assert.Equal(1, result.ImportedCount);

        var txn = await db.Transactions.FirstAsync(t => t.AccountId == card.Id);
        Assert.Equal(-7.95m, txn.Amount);

        var expectedHash = CsvImportService.ComputeImportHash(card.Id, new DateOnly(2026, 7, 1), -7.95m, "Whole Foods");
        Assert.Equal(expectedHash, txn.ImportHash);
    }

    [Fact]
    public void SuggestInvertAmounts_LiabilityWithMostlyPositiveAmounts_ReturnsTrue()
    {
        using var db = CreateContext();
        var service = new CsvImportService(db);

        var headers = new List<string> { "Trans. Date", "Description", "Amount" };
        var rows = new List<List<string>>
        {
            new() { "07/01/2026", "Whole Foods", "7.95" },
            new() { "07/02/2026", "Gas Station", "42.10" },
            new() { "07/03/2026", "Payment", "-500.00" }
        };

        Assert.True(service.SuggestInvertAmounts(accountIsLiability: true, headers, rows));
    }

    [Fact]
    public void SuggestInvertAmounts_NonLiability_ReturnsFalse()
    {
        using var db = CreateContext();
        var service = new CsvImportService(db);

        var headers = new List<string> { "Trans. Date", "Description", "Amount" };
        var rows = new List<List<string>>
        {
            new() { "07/01/2026", "Whole Foods", "7.95" },
            new() { "07/02/2026", "Gas Station", "42.10" }
        };

        Assert.False(service.SuggestInvertAmounts(accountIsLiability: false, headers, rows));
    }

    [Fact]
    public void SuggestInvertAmounts_LiabilityWithMostlyNegativeAmounts_ReturnsFalse()
    {
        using var db = CreateContext();
        var service = new CsvImportService(db);

        var headers = new List<string> { "Trans. Date", "Description", "Amount" };
        var rows = new List<List<string>>
        {
            new() { "07/01/2026", "Whole Foods", "-7.95" },
            new() { "07/02/2026", "Gas Station", "-42.10" },
            new() { "07/03/2026", "Payment", "500.00" }
        };

        Assert.False(service.SuggestInvertAmounts(accountIsLiability: true, headers, rows));
    }

    [Fact]
    public async Task InvertAccountTransactionSignsAsync_FlipsAmountsAndRecomputesHash()
    {
        using var db = CreateContext();
        var card = new Account { UserId = UserId, Name = "Discover", Type = AccountType.CreditCard };
        db.Accounts.Add(card);
        await db.SaveChangesAsync();

        var date = new DateOnly(2026, 7, 1);
        var originalHash = CsvImportService.ComputeImportHash(card.Id, date, 7.95m, "Whole Foods");
        var txn = new Transaction
        {
            AccountId = card.Id,
            Date = date,
            Amount = 7.95m,
            Description = "Whole Foods",
            OriginalDescription = "Whole Foods",
            ImportHash = originalHash
        };
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();

        var service = new DataManagementService(db);
        var count = await service.InvertAccountTransactionSignsAsync(card.Id);

        Assert.Equal(1, count);

        var updated = await db.Transactions.FirstAsync(t => t.Id == txn.Id);
        Assert.Equal(-7.95m, updated.Amount);
        var expectedHash = CsvImportService.ComputeImportHash(card.Id, date, -7.95m, "Whole Foods");
        Assert.Equal(expectedHash, updated.ImportHash);
        Assert.NotEqual(originalHash, updated.ImportHash);
    }
}
