using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

/// <summary>
/// Optimistic-concurrency (RowVersion) coverage: a save carrying a stale token — the
/// classic "two tabs editing the same record" case — must throw
/// DbUpdateConcurrencyException, while a save with the current token succeeds and
/// re-stamps the token.
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "concurrency-user";
    private readonly int _accountId;
    private readonly int _transactionId;

    public ConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var db = new ApplicationDbContext(_options);
        db.Database.EnsureCreated();
        db.Users.Add(new IdentityUser { Id = UserId, UserName = "c@test.com", Email = "c@test.com", NormalizedEmail = "C@TEST.COM" });
        db.SaveChanges();

        var account = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.Add(account);
        db.SaveChanges();
        _accountId = account.Id;

        var txn = new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -10m, Description = "Seed" };
        db.Transactions.Add(txn);
        db.SaveChanges();
        _transactionId = txn.Id;
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext CreateContext()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    [Fact]
    public void SeededRows_HaveNonEmptyRowVersion()
    {
        using var db = CreateContext();
        var txn = db.Transactions.AsNoTracking().First(t => t.Id == _transactionId);
        var account = db.Accounts.AsNoTracking().First(a => a.Id == _accountId);
        Assert.NotEqual(Guid.Empty, txn.RowVersion);
        Assert.NotEqual(Guid.Empty, account.RowVersion);
    }

    [Fact]
    public async Task Update_RestampsRowVersion()
    {
        Guid before;
        using (var db = CreateContext())
        {
            before = (await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == _transactionId)).RowVersion;
        }

        using (var db = CreateContext())
        {
            await new TransactionService(db).UpdateAsync(_transactionId, _accountId, new DateOnly(2026, 1, 1), -20m, "Changed");
        }

        using var check = CreateContext();
        var after = (await check.Transactions.AsNoTracking().FirstAsync(t => t.Id == _transactionId)).RowVersion;
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Transaction_StaleRowVersion_ThrowsConcurrencyException()
    {
        // A user opens the edit form and captures the current token.
        Guid staleVersion;
        using (var db = CreateContext())
        {
            staleVersion = (await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == _transactionId)).RowVersion;
        }

        // Another writer saves first, bumping the token in the database.
        using (var db = CreateContext())
        {
            await new TransactionService(db).UpdateAsync(_transactionId, _accountId, new DateOnly(2026, 1, 1), -30m, "First writer");
        }

        // The original edit posts with its now-stale token → conflict.
        using var conflict = CreateContext();
        var service = new TransactionService(conflict);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            service.UpdateAsync(_transactionId, _accountId, new DateOnly(2026, 1, 1), -40m, "Second writer", staleVersion));
    }

    [Fact]
    public async Task Transaction_CurrentRowVersion_Succeeds()
    {
        Guid current;
        using (var db = CreateContext())
        {
            current = (await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == _transactionId)).RowVersion;
        }

        using var db2 = CreateContext();
        var ok = await new TransactionService(db2).UpdateAsync(_transactionId, _accountId, new DateOnly(2026, 1, 1), -50m, "Fresh", current);
        Assert.True(ok);
    }

    [Fact]
    public async Task Account_StaleRowVersion_ThrowsConcurrencyException()
    {
        Guid staleVersion;
        using (var db = CreateContext())
        {
            staleVersion = (await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == _accountId)).RowVersion;
        }

        using (var db = CreateContext())
        {
            await new AccountService(db).UpdateAsync(_accountId, "First writer", AccountType.Checking, null);
        }

        using var conflict = CreateContext();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new AccountService(conflict).UpdateAsync(_accountId, "Second writer", AccountType.Savings, null, staleVersion));
    }
}
