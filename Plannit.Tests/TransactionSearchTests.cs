using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class TransactionSearchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "user-1";
    private readonly int _accountId;

    public TransactionSearchTests()
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

        var account = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        seedDb.Accounts.Add(account);
        seedDb.SaveChanges();
        _accountId = account.Id;

        seedDb.Transactions.AddRange(
            new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 5), Amount = -10, Description = "TARGET T-1234" },
            new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 6), Amount = -20, Description = "Amazon.com*RC3GY2873" },
            new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 7), Amount = -30, Description = "50% off coupon" }
        );
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
    public async Task GetFilteredAsync_SearchIsCaseInsensitive()
    {
        using var db = CreateContext();
        var service = new TransactionService(db);

        var (items, total) = await service.GetFilteredAsync(null, null, null, "target", null, 1);

        Assert.Equal(1, total);
        Assert.Equal("TARGET T-1234", items[0].Description);
    }

    [Fact]
    public async Task GetFilteredAsync_SearchMatchesMixedCaseSubstring()
    {
        using var db = CreateContext();
        var service = new TransactionService(db);

        var (items, total) = await service.GetFilteredAsync(null, null, null, "AMAZON.com", null, 1);

        Assert.Equal(1, total);
        Assert.Contains(items, t => t.Description == "Amazon.com*RC3GY2873");
    }

    [Fact]
    public async Task GetFilteredAsync_TreatsPercentInSearchTextAsLiteral()
    {
        using var db = CreateContext();
        var service = new TransactionService(db);

        // If "%" were left as a SQL wildcard rather than escaped, this would match all 3 seeded rows.
        var (items, total) = await service.GetFilteredAsync(null, null, null, "%", null, 1);

        Assert.Equal(1, total);
        Assert.Equal("50% off coupon", items[0].Description);
    }
}
