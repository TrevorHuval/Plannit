using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class NotificationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "notif-user";
    private readonly int _accountId;

    public NotificationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var db = new ApplicationDbContext(_options);
        db.Database.EnsureCreated();
        db.Users.Add(new IdentityUser { Id = UserId, UserName = "notif@test.com", Email = "notif@test.com", NormalizedEmail = "NOTIF@TEST.COM" });
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

    private static NotificationService CreateNotificationService(ApplicationDbContext db)
    {
        var billService = new BillService(db);
        var budgetService = new BudgetService(db);
        var forecastService = new ForecastService(db, billService);
        var netWorthService = new NetWorthService(db, new MemoryCache(new MemoryCacheOptions()));
        return new NotificationService(db, new NoOpEmailSender(), budgetService, billService, forecastService, netWorthService, NullLogger<NotificationService>.Instance);
    }

    private class NoOpEmailSender : IEmailSender
    {
        public bool IsConfigured => false;
        public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ===== Budget overage =====

    [Fact]
    public async Task RunDailyChecksAsync_BudgetOverBudget_CreatesNotification_AndDedupsOnSecondRun()
    {
        using (var seedDb = CreateContext())
        {
            var category = new Category { UserId = UserId, Name = "Dining" };
            seedDb.Categories.Add(category);
            await seedDb.SaveChangesAsync();

            seedDb.Budgets.Add(new Budget
            {
                UserId = UserId,
                CategoryId = category.Id,
                MonthlyAmount = 100m,
                StartMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1)
            });
            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = DateOnly.FromDateTime(DateTime.Today),
                Amount = -150m,
                Description = "Restaurant",
                CategoryId = category.Id
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);

        await service.RunDailyChecksAsync(UserId);
        var firstRun = await db.Notifications.Where(n => n.Type == NotificationType.BudgetOverage).ToListAsync();
        Assert.Single(firstRun);

        await service.RunDailyChecksAsync(UserId);
        var secondRun = await db.Notifications.Where(n => n.Type == NotificationType.BudgetOverage).ToListAsync();
        Assert.Single(secondRun);
    }

    [Fact]
    public async Task RunDailyChecksAsync_BudgetUnderLimit_CreatesNoNotification()
    {
        using (var seedDb = CreateContext())
        {
            var category = new Category { UserId = UserId, Name = "Dining" };
            seedDb.Categories.Add(category);
            await seedDb.SaveChangesAsync();

            seedDb.Budgets.Add(new Budget
            {
                UserId = UserId,
                CategoryId = category.Id,
                MonthlyAmount = 200m,
                StartMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1)
            });
            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = DateOnly.FromDateTime(DateTime.Today),
                Amount = -50m,
                Description = "Restaurant",
                CategoryId = category.Id
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);
        await service.RunDailyChecksAsync(UserId);

        Assert.Empty(await db.Notifications.Where(n => n.Type == NotificationType.BudgetOverage).ToListAsync());
    }

    // ===== Bill due soon =====

    [Fact]
    public async Task RunDailyChecksAsync_BillDueWithinThreeDays_CreatesNotification_AndDedupsOnSecondRun()
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
                NextDue = DateOnly.FromDateTime(DateTime.Today).AddDays(2),
                IsIncome = false,
                Source = BillSource.Manual,
                IsActive = true
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);

        await service.RunDailyChecksAsync(UserId);
        var firstRun = await db.Notifications.Where(n => n.Type == NotificationType.BillDue).ToListAsync();
        Assert.Single(firstRun);

        await service.RunDailyChecksAsync(UserId);
        var secondRun = await db.Notifications.Where(n => n.Type == NotificationType.BillDue).ToListAsync();
        Assert.Single(secondRun);
    }

    [Fact]
    public async Task RunDailyChecksAsync_BillDueFarInFuture_CreatesNoNotification()
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
                NextDue = DateOnly.FromDateTime(DateTime.Today).AddDays(20),
                IsIncome = false,
                Source = BillSource.Manual,
                IsActive = true
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);
        await service.RunDailyChecksAsync(UserId);

        Assert.Empty(await db.Notifications.Where(n => n.Type == NotificationType.BillDue).ToListAsync());
    }

    // ===== Stale accounts =====

    [Fact]
    public async Task RunDailyChecksAsync_AccountWithNoSnapshot_CreatesStaleNotification()
    {
        using var db = CreateContext();
        var service = CreateNotificationService(db);

        await service.RunDailyChecksAsync(UserId);
        var notifications = await db.Notifications.Where(n => n.Type == NotificationType.StaleAccount).ToListAsync();

        Assert.Single(notifications);
        Assert.Contains("Checking", notifications[0].Message);
    }

    // ===== Import-time large/anomalous transaction =====

    [Fact]
    public async Task CheckImportAnomaliesAsync_TransactionFarAboveCategoryAverage_CreatesNotification_AndDedupsOnSecondCall()
    {
        int batchId;
        using (var seedDb = CreateContext())
        {
            var category = new Category { UserId = UserId, Name = "Groceries" };
            seedDb.Categories.Add(category);
            await seedDb.SaveChangesAsync();

            // Historical, already-categorized transactions establish a ~$50 average — none are
            // part of the new import batch, so they form the baseline the new one is compared to.
            seedDb.Transactions.AddRange(
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -50m, Description = "Kroger", CategoryId = category.Id },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 2, 1), Amount = -48m, Description = "Kroger", CategoryId = category.Id },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 3, 1), Amount = -52m, Description = "Kroger", CategoryId = category.Id }
            );
            await seedDb.SaveChangesAsync();

            var batch = new ImportBatch { AccountId = _accountId, FileName = "test.csv", ImportedAt = DateTime.UtcNow };
            seedDb.ImportBatches.Add(batch);
            await seedDb.SaveChangesAsync();
            batchId = batch.Id;

            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = new DateOnly(2026, 4, 1),
                Amount = -500m,
                Description = "Whole Foods",
                CategoryId = category.Id,
                ImportBatchId = batchId
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);

        var createdFirst = await service.CheckImportAnomaliesAsync(batchId);
        Assert.Equal(1, createdFirst);
        Assert.Single(await db.Notifications.Where(n => n.Type == NotificationType.LargeTransaction).ToListAsync());

        var createdSecond = await service.CheckImportAnomaliesAsync(batchId);
        Assert.Equal(0, createdSecond);
        Assert.Single(await db.Notifications.Where(n => n.Type == NotificationType.LargeTransaction).ToListAsync());
    }

    [Fact]
    public async Task CheckImportAnomaliesAsync_TransactionWithinNormalRange_CreatesNoNotification()
    {
        int batchId;
        using (var seedDb = CreateContext())
        {
            var category = new Category { UserId = UserId, Name = "Groceries" };
            seedDb.Categories.Add(category);
            await seedDb.SaveChangesAsync();

            seedDb.Transactions.AddRange(
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -50m, Description = "Kroger", CategoryId = category.Id },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 2, 1), Amount = -48m, Description = "Kroger", CategoryId = category.Id },
                new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 3, 1), Amount = -52m, Description = "Kroger", CategoryId = category.Id }
            );
            await seedDb.SaveChangesAsync();

            var batch = new ImportBatch { AccountId = _accountId, FileName = "test.csv", ImportedAt = DateTime.UtcNow };
            seedDb.ImportBatches.Add(batch);
            await seedDb.SaveChangesAsync();
            batchId = batch.Id;

            seedDb.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = new DateOnly(2026, 4, 1),
                Amount = -60m,
                Description = "Kroger",
                CategoryId = category.Id,
                ImportBatchId = batchId
            });
            await seedDb.SaveChangesAsync();
        }

        using var db = CreateContext();
        var service = CreateNotificationService(db);

        var created = await service.CheckImportAnomaliesAsync(batchId);
        Assert.Equal(0, created);
        Assert.Empty(await db.Notifications.Where(n => n.Type == NotificationType.LargeTransaction).ToListAsync());
    }
}
