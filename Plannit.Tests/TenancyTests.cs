using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

public class TenancyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly string _userAId = "user-a-id";
    private readonly string _userBId = "user-b-id";

    private readonly int _accountAId;
    private readonly int _accountBId;
    private readonly int _categoryAId;
    private readonly int _categoryBId;
    private readonly int _scenarioAId;
    private readonly int _scenarioBId;
    private readonly int _transactionAId;
    private readonly int _snapshotAId;
    private readonly int _billAId;

    public TenancyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var seedDb = new ApplicationDbContext(_options))
        {
            seedDb.Database.EnsureCreated();

            var userA = new IdentityUser { Id = _userAId, UserName = "userA@test.com", Email = "userA@test.com", NormalizedEmail = "USERA@TEST.COM" };
            var userB = new IdentityUser { Id = _userBId, UserName = "userB@test.com", Email = "userB@test.com", NormalizedEmail = "USERB@TEST.COM" };
            seedDb.Users.AddRange(userA, userB);
            seedDb.SaveChanges();

            var accountA = new Account { UserId = _userAId, Name = "A Checking", Type = AccountType.Checking };
            var accountB = new Account { UserId = _userBId, Name = "B Checking", Type = AccountType.Checking };
            seedDb.Accounts.AddRange(accountA, accountB);
            seedDb.SaveChanges();
            _accountAId = accountA.Id;
            _accountBId = accountB.Id;

            var snapshotA = new BalanceSnapshot { AccountId = _accountAId, Date = new DateOnly(2026, 1, 1), Balance = 1000 };
            seedDb.BalanceSnapshots.AddRange(
                snapshotA,
                new BalanceSnapshot { AccountId = _accountBId, Date = new DateOnly(2026, 1, 1), Balance = 2000 }
            );
            seedDb.SaveChanges();
            _snapshotAId = snapshotA.Id;

            var batchA = new ImportBatch { AccountId = _accountAId, FileName = "a.csv", ImportedAt = DateTime.UtcNow, RowCount = 1 };
            var batchB = new ImportBatch { AccountId = _accountBId, FileName = "b.csv", ImportedAt = DateTime.UtcNow, RowCount = 1 };
            seedDb.ImportBatches.AddRange(batchA, batchB);
            seedDb.SaveChanges();

            var txnA = new Transaction { AccountId = _accountAId, Date = new DateOnly(2026, 1, 5), Amount = -50, Description = "Store A", ImportBatchId = batchA.Id };
            seedDb.Transactions.AddRange(
                txnA,
                new Transaction { AccountId = _accountBId, Date = new DateOnly(2026, 1, 5), Amount = -75, Description = "Store B", ImportBatchId = batchB.Id }
            );
            seedDb.SaveChanges();
            _transactionAId = txnA.Id;

            var categoryA = new Category { UserId = _userAId, Name = "Cat A" };
            var categoryB = new Category { UserId = _userBId, Name = "Cat B" };
            seedDb.Categories.AddRange(categoryA, categoryB);
            seedDb.SaveChanges();
            _categoryAId = categoryA.Id;
            _categoryBId = categoryB.Id;

            seedDb.CategoryRules.AddRange(
                new CategoryRule { UserId = _userAId, MatchText = "Store A", MatchType = Plannit.Models.Entities.MatchType.Contains, CategoryId = _categoryAId, Priority = 1 },
                new CategoryRule { UserId = _userBId, MatchText = "Store B", MatchType = Plannit.Models.Entities.MatchType.Contains, CategoryId = _categoryBId, Priority = 1 }
            );
            seedDb.SaveChanges();

            var scenarioA = new ProjectionScenario { UserId = _userAId, Name = "Scenario A", BirthYear = 1990, RetirementAge = 65, LifeExpectancy = 90, AnnualRetirementSpending = 40000, InflationRate = 0.03m };
            var scenarioB = new ProjectionScenario { UserId = _userBId, Name = "Scenario B", BirthYear = 1985, RetirementAge = 60, LifeExpectancy = 85, AnnualRetirementSpending = 50000, InflationRate = 0.02m };
            seedDb.ProjectionScenarios.AddRange(scenarioA, scenarioB);
            seedDb.SaveChanges();
            _scenarioAId = scenarioA.Id;
            _scenarioBId = scenarioB.Id;

            seedDb.ProjectionAccountAssumptions.AddRange(
                new ProjectionAccountAssumption { ScenarioId = _scenarioAId, AccountId = _accountAId, AnnualContribution = 10000, EmployerMatch = 5000, ExpectedReturnRate = 0.07m, ContributionEndAge = 65 },
                new ProjectionAccountAssumption { ScenarioId = _scenarioBId, AccountId = _accountBId, AnnualContribution = 8000, EmployerMatch = 4000, ExpectedReturnRate = 0.06m, ContributionEndAge = 60 }
            );
            seedDb.SaveChanges();

            seedDb.ImportProfiles.AddRange(
                new ImportProfile { AccountId = _accountAId, DateColumn = "Date", DateFormat = "MM/dd/yyyy", AmountColumn = "Amount", DescriptionColumn = "Desc" },
                new ImportProfile { AccountId = _accountBId, DateColumn = "Date", DateFormat = "yyyy-MM-dd", AmountColumn = "Amount", DescriptionColumn = "Desc" }
            );
            seedDb.SaveChanges();

            var billA = new Bill { UserId = _userAId, MerchantKey = "NETFLIX", Name = "Netflix", Cadence = RecurringCadence.Monthly, ExpectedAmount = 15.99m, NextDue = new DateOnly(2026, 2, 1), IsIncome = false, Source = BillSource.Manual };
            var billB = new Bill { UserId = _userBId, MerchantKey = "SPOTIFY", Name = "Spotify", Cadence = RecurringCadence.Monthly, ExpectedAmount = 9.99m, NextDue = new DateOnly(2026, 2, 1), IsIncome = false, Source = BillSource.Manual };
            seedDb.Bills.AddRange(billA, billB);
            seedDb.SaveChanges();
            _billAId = billA.Id;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private ApplicationDbContext CreateContext(string userId)
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(userId);
        return db;
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_Accounts()
    {
        using var db = CreateContext(_userBId);
        var accounts = await db.Accounts.ToListAsync();
        Assert.All(accounts, a => Assert.Equal(_userBId, a.UserId));
        Assert.DoesNotContain(accounts, a => a.Id == _accountAId);
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_Transactions()
    {
        using var db = CreateContext(_userBId);
        var transactions = await db.Transactions.ToListAsync();
        Assert.All(transactions, t => Assert.Equal(_accountBId, t.AccountId));
        Assert.DoesNotContain(transactions, t => t.Description == "Store A");
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_BalanceSnapshots()
    {
        using var db = CreateContext(_userBId);
        var snapshots = await db.BalanceSnapshots.ToListAsync();
        Assert.All(snapshots, s => Assert.Equal(_accountBId, s.AccountId));
        Assert.DoesNotContain(snapshots, s => s.Balance == 1000);
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_ImportBatches()
    {
        using var db = CreateContext(_userBId);
        var batches = await db.ImportBatches.ToListAsync();
        Assert.All(batches, b => Assert.Equal(_accountBId, b.AccountId));
        Assert.DoesNotContain(batches, b => b.FileName == "a.csv");
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_ImportProfiles()
    {
        using var db = CreateContext(_userBId);
        var profiles = await db.ImportProfiles.ToListAsync();
        Assert.All(profiles, p => Assert.Equal(_accountBId, p.AccountId));
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_Bills()
    {
        using var db = CreateContext(_userBId);
        var bills = await db.Bills.ToListAsync();
        Assert.All(bills, b => Assert.Equal(_userBId, b.UserId));
        Assert.DoesNotContain(bills, b => b.Id == _billAId);
    }

    [Fact]
    public async Task BillService_GetByIdAsync_ReturnsNull_ForOtherUsersBill()
    {
        using var db = CreateContext(_userBId);
        var service = new BillService(db);

        var bill = await service.GetByIdAsync(_billAId);
        Assert.Null(bill);

        var updated = await service.UpdateAsync(_billAId, "Hijacked", RecurringCadence.Monthly, 1m, new DateOnly(2026, 3, 1), false);
        Assert.False(updated);

        using var dbA = CreateContext(_userAId);
        var stillOwnedByA = await dbA.Bills.FirstAsync(b => b.Id == _billAId);
        Assert.Equal("Netflix", stillOwnedByA.Name);
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_Categories()
    {
        using var db = CreateContext(_userBId);
        var categories = await db.Categories.ToListAsync();
        Assert.All(categories, c => Assert.Equal(_userBId, c.UserId));
        Assert.DoesNotContain(categories, c => c.Name == "Cat A");
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_CategoryRules()
    {
        using var db = CreateContext(_userBId);
        var rules = await db.CategoryRules.ToListAsync();
        Assert.All(rules, r => Assert.Equal(_userBId, r.UserId));
        Assert.DoesNotContain(rules, r => r.MatchText == "Store A");
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_ProjectionScenarios()
    {
        using var db = CreateContext(_userBId);
        var scenarios = await db.ProjectionScenarios.ToListAsync();
        Assert.All(scenarios, s => Assert.Equal(_userBId, s.UserId));
        Assert.DoesNotContain(scenarios, s => s.Name == "Scenario A");
    }

    [Fact]
    public async Task UserB_CannotSee_UserA_ProjectionAccountAssumptions()
    {
        using var db = CreateContext(_userBId);
        var assumptions = await db.ProjectionAccountAssumptions.ToListAsync();
        Assert.All(assumptions, a => Assert.Equal(_scenarioBId, a.ScenarioId));
    }

    [Fact]
    public async Task UserB_CannotRead_UserA_Account_ById()
    {
        using var db = CreateContext(_userBId);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == _accountAId);
        Assert.Null(account);
    }

    [Fact]
    public async Task UserB_CannotRead_UserA_Scenario_ById()
    {
        using var db = CreateContext(_userBId);
        var scenario = await db.ProjectionScenarios.FirstOrDefaultAsync(s => s.Id == _scenarioAId);
        Assert.Null(scenario);
    }

    [Fact]
    public async Task UserB_CannotRead_UserA_Category_ById()
    {
        using var db = CreateContext(_userBId);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == _categoryAId);
        Assert.Null(category);
    }

    [Fact]
    public async Task AccountService_ReturnsOnly_CurrentUser_Accounts()
    {
        using var db = CreateContext(_userBId);
        var service = new AccountService(db);
        var accounts = await service.GetAllAsync();
        Assert.All(accounts, a => Assert.Equal(_userBId, a.UserId));
    }

    [Fact]
    public async Task AccountService_GetById_ReturnsNull_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new AccountService(db);
        var account = await service.GetByIdAsync(_accountAId);
        Assert.Null(account);
    }

    [Fact]
    public async Task AccountService_Update_Fails_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new AccountService(db);
        var result = await service.UpdateAsync(_accountAId, "Hacked", AccountType.Savings, null);
        Assert.False(result);
    }

    [Fact]
    public async Task AccountService_Deactivate_Fails_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new AccountService(db);
        var result = await service.DeactivateAsync(_accountAId);
        Assert.False(result);
    }

    [Fact]
    public async Task TransactionService_ReturnsOnly_CurrentUser_Transactions()
    {
        using var db = CreateContext(_userBId);
        var service = new TransactionService(db);
        var (items, count) = await service.GetFilteredAsync(null, null, null, null, null, 1);
        Assert.All(items, t => Assert.Equal(_accountBId, t.AccountId));
    }

    [Fact]
    public async Task TransactionService_Update_Fails_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new TransactionService(db);
        var result = await service.UpdateAsync(_transactionAId, _accountBId, new DateOnly(2026, 1, 1), -999, "hacked");
        Assert.False(result);
    }

    [Fact]
    public async Task TransactionService_Delete_Fails_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new TransactionService(db);
        var result = await service.DeleteAsync(_transactionAId);
        Assert.False(result);
    }

    [Fact]
    public async Task CategorizationService_ReturnsOnly_CurrentUser_Categories()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var categories = await service.GetAllCategoriesAsync();
        Assert.All(categories, c => Assert.Equal(_userBId, c.UserId));
    }

    [Fact]
    public async Task ProjectionService_ReturnsOnly_CurrentUser_Scenarios()
    {
        using var db = CreateContext(_userBId);
        var service = new ProjectionService(db);
        var scenarios = await service.GetScenariosAsync();
        Assert.All(scenarios, s => Assert.Equal(_userBId, s.UserId));
    }

    [Fact]
    public async Task ProjectionService_GetScenario_ReturnsNull_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new ProjectionService(db);
        var scenario = await service.GetScenarioAsync(_scenarioAId);
        Assert.Null(scenario);
    }

    [Fact]
    public async Task ProjectionService_Delete_NoOp_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new ProjectionService(db);
        await service.DeleteScenarioAsync(_scenarioAId);

        using var dbA = CreateContext(_userAId);
        var stillExists = await dbA.ProjectionScenarios.FirstOrDefaultAsync(s => s.Id == _scenarioAId);
        Assert.NotNull(stillExists);
    }

    [Fact]
    public async Task ReportsService_ExcludesOtherUser_Data()
    {
        using var db = CreateContext(_userBId);
        var service = new ReportsService(db);
        var result = await service.GetSpendByCategoryAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(75m, result.TotalSpend);
    }

    [Fact]
    public async Task NetWorthService_ExcludesOtherUser_Data()
    {
        using var db = CreateContext(_userBId);
        var service = new NetWorthService(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var netWorth = await service.GetCurrentNetWorthAsync();
        Assert.Equal(2000m, netWorth);
    }

    [Fact]
    public async Task AccountService_DeleteSnapshot_Fails_ForOtherUser()
    {
        using var db = CreateContext(_userBId);
        var service = new AccountService(db);
        var result = await service.DeleteSnapshotAsync(_snapshotAId);
        Assert.False(result);
    }

    // Write-path isolation: the global query filters only scope reads, so every
    // service must reject posted foreign keys that reference another user's rows.

    [Fact]
    public async Task TransactionService_Create_Fails_ForOtherUsersAccount()
    {
        using var db = CreateContext(_userBId);
        var service = new TransactionService(db);
        var created = await service.CreateAsync(_accountAId, new DateOnly(2026, 2, 1), -25, "Injected");
        Assert.Null(created);

        using var dbA = CreateContext(_userAId);
        Assert.False(await dbA.Transactions.AnyAsync(t => t.Description == "Injected"));
    }

    [Fact]
    public async Task TransactionService_Update_CannotMoveTransaction_IntoOtherUsersAccount()
    {
        using var db = CreateContext(_userBId);
        var service = new TransactionService(db);
        var txnB = await db.Transactions.FirstAsync();

        var result = await service.UpdateAsync(txnB.Id, _accountAId, txnB.Date, txnB.Amount, txnB.Description);
        Assert.False(result);

        using var dbB = CreateContext(_userBId);
        var unchanged = await dbB.Transactions.FirstAsync(t => t.Id == txnB.Id);
        Assert.Equal(_accountBId, unchanged.AccountId);
    }

    [Fact]
    public async Task DataManagementService_BulkSetCategory_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var txnB = await db.Transactions.FirstAsync();
        var service = new DataManagementService(db);

        var count = await service.BulkSetCategoryAsync([txnB.Id], _categoryAId);
        Assert.Equal(0, count);

        using var dbB = CreateContext(_userBId);
        var unchanged = await dbB.Transactions.FirstAsync(t => t.Id == txnB.Id);
        Assert.Null(unchanged.CategoryId);
    }

    [Fact]
    public async Task DataManagementService_Split_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var txnB = await db.Transactions.FirstAsync();
        var service = new DataManagementService(db);

        var result = await service.SplitTransactionAsync(txnB.Id,
            [(txnB.Amount / 2, "Part 1", _categoryAId), (txnB.Amount - txnB.Amount / 2, "Part 2", null)]);
        Assert.Empty(result);

        using var dbB = CreateContext(_userBId);
        Assert.NotNull(await dbB.Transactions.FirstOrDefaultAsync(t => t.Id == txnB.Id));
    }

    [Fact]
    public async Task BudgetService_CreateOrUpdate_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var service = new BudgetService(db);

        var budget = await service.CreateOrUpdateBudgetAsync(_userBId, _categoryAId, 500);
        Assert.Null(budget);

        using var dbA = CreateContext(_userAId);
        Assert.False(await dbA.Budgets.AnyAsync(b => b.CategoryId == _categoryAId));
    }

    [Fact]
    public async Task CategorizationService_CreateRule_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var rule = await service.CreateRuleAsync(_userBId, "Injected", Plannit.Models.Entities.MatchType.Contains, _categoryAId, 1);
        Assert.Null(rule);
    }

    [Fact]
    public async Task CategorizationService_UpdateRule_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var ruleB = await db.CategoryRules.FirstAsync();

        var result = await service.UpdateRuleAsync(ruleB.Id, ruleB.MatchText, ruleB.MatchType, _categoryAId, ruleB.Priority);
        Assert.False(result);
    }

    [Fact]
    public async Task CategorizationService_CategorizeTransaction_Fails_ForOtherUsersCategory()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var txnB = await db.Transactions.FirstAsync();

        var count = await service.CategorizeTransactionAsync(txnB.Id, _categoryAId);
        Assert.Equal(0, count);

        using var dbB = CreateContext(_userBId);
        var unchanged = await dbB.Transactions.FirstAsync(t => t.Id == txnB.Id);
        Assert.Null(unchanged.CategoryId);
    }

    [Fact]
    public async Task CategorizationService_CreateCategory_Fails_ForOtherUsersParent()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var category = await service.CreateCategoryAsync(_userBId, "Injected", _categoryAId);
        Assert.Null(category);
    }

    [Fact]
    public async Task CategorizationService_UpdateCategory_Fails_ForOtherUsersParent()
    {
        using var db = CreateContext(_userBId);
        var service = new CategorizationService(db);
        var result = await service.UpdateCategoryAsync(_categoryBId, "Cat B", _categoryAId);
        Assert.False(result);
    }

    // Fail-closed filters: a context with no current user set (anonymous / not-yet-authenticated)
    // must see zero rows across every filtered entity, not "everyone's" rows.

    [Fact]
    public async Task AnonymousContext_SeesNoRows_AcrossAllFilteredEntities()
    {
        using var db = new ApplicationDbContext(_options);

        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.BalanceSnapshots.ToListAsync());
        Assert.Empty(await db.ImportBatches.ToListAsync());
        Assert.Empty(await db.ImportProfiles.ToListAsync());
        Assert.Empty(await db.Categories.ToListAsync());
        Assert.Empty(await db.CategoryRules.ToListAsync());
        Assert.Empty(await db.ProjectionScenarios.ToListAsync());
        Assert.Empty(await db.ProjectionAccountAssumptions.ToListAsync());
        Assert.Empty(await db.Budgets.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
        Assert.Empty(await db.Bills.ToListAsync());
    }

    [Fact]
    public async Task AccountService_ReturnsNoAccounts_ForAnonymousContext()
    {
        using var db = new ApplicationDbContext(_options);
        var service = new AccountService(db);
        var accounts = await service.GetAllAsync();
        Assert.Empty(accounts);
    }
}
