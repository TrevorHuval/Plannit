using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Services.Ai;

namespace Plannit.Tests;

public class SmartCategorizeApplyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private const string UserId = "user-1";
    private int _accountId;

    public SmartCategorizeApplyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();

        db.Users.Add(new IdentityUser { Id = UserId, UserName = "u@test.com", Email = "u@test.com" });
        db.SaveChanges();

        var account = new Account { UserId = UserId, Name = "Card", Type = AccountType.CreditCard };
        db.Accounts.Add(account);
        db.SaveChanges();
        _accountId = account.Id;

        db.Categories.Add(new Category { UserId = UserId, Name = "Entertainment" });
        db.SaveChanges();

        // 3 Netflix (existing-category target), 5 Chewy (new-category target), 1 obscure (unsure).
        AddTxns(db, "Netflix", -15.99m, 3);
        AddTxns(db, "Chewy Pet Supplies", -48.20m, 5);
        AddTxns(db, "Obscure Vendor XYZ", -9.00m, 1);
        db.SaveChanges();
    }

    private ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    private void AddTxns(ApplicationDbContext db, string description, decimal amount, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Transactions.Add(new Transaction
            {
                AccountId = _accountId,
                Date = new DateOnly(2026, 6, 1).AddDays(i),
                Amount = amount,
                Description = description
            });
        }
    }

    // Fake provider: returns canned JSON, run through the real prompt/parser pipeline.
    private sealed class FakeCategorizer : PromptBasedCategorizer
    {
        private readonly string _json;
        public FakeCategorizer(string json) => _json = json;
        public override string Name => "Fake";
        protected override Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(_json);
    }

    [Fact]
    public async Task EndToEnd_AppliesConfidentProposals_CreatesCategoryAndRules_LeavesUnsureUntouched()
    {
        List<CategoryProposal> proposals;

        // 1) Gather + provider call.
        await using (var db = NewDb())
        {
            var categorization = new CategorizationService(db);
            var smart = new SmartCategorizationService(db, categorization);

            var groups = await smart.GatherAsync();
            var existing = await smart.GetExistingCategoryNamesAsync();
            var request = smart.BuildRequest(groups, existing);

            var json = """
            {"suggestions":[
              {"merchantKey":"NETFLIX","category":"Entertainment","confidence":0.95,"isNew":false},
              {"merchantKey":"CHEWY PET SUPPLIES","category":"Pet Care","confidence":0.85,"isNew":true},
              {"merchantKey":"OBSCURE VENDOR","category":null,"confidence":0.2,"isNew":false}
            ]}
            """;
            var result = await new FakeCategorizer(json).CategorizeAsync(request);
            Assert.True(result.Success);
            proposals = result.Proposals.ToList();
        }

        // 2) Accept confident proposals (mirrors the controller's >= 0.8 pre-check) and apply.
        var accepted = proposals
            .Where(p => p.CategoryName is not null && p.Confidence >= 0.8)
            .Select(p => new SmartCategorizationService.AcceptedMapping(p.MerchantKey, p.CategoryName!, p.IsNewCategorySuggestion, CreateRule: true))
            .ToList();

        SmartCategorizationService.ApplyResult applyResult;
        await using (var db = NewDb())
        {
            var smart = new SmartCategorizationService(db, new CategorizationService(db));
            applyResult = await smart.ApplyAsync(UserId, accepted);
        }

        Assert.Equal(1, applyResult.CategoriesCreated);   // Pet Care
        Assert.Equal(8, applyResult.TransactionsUpdated); // 3 Netflix + 5 Chewy
        Assert.Equal(2, applyResult.RulesCreated);

        // 3) Verify database state.
        await using (var db = NewDb())
        {
            var entertainment = await db.Categories.FirstAsync(c => c.Name == "Entertainment");
            var petCare = await db.Categories.FirstAsync(c => c.Name == "Pet Care");

            Assert.Equal(3, await db.Transactions.CountAsync(t => t.Description == "Netflix" && t.CategoryId == entertainment.Id));
            Assert.Equal(5, await db.Transactions.CountAsync(t => t.Description == "Chewy Pet Supplies" && t.CategoryId == petCare.Id));

            // Unsure merchant stays uncategorized.
            Assert.Equal(1, await db.Transactions.CountAsync(t => t.Description == "Obscure Vendor XYZ" && t.CategoryId == null));

            // Category cap respected.
            Assert.True(await db.Categories.CountAsync() <= 25);

            // Rules were created for both accepted merchants.
            Assert.True(await db.CategoryRules.AnyAsync(r => r.MatchText == "NETFLIX" && r.CategoryId == entertainment.Id));
            Assert.True(await db.CategoryRules.AnyAsync(r => r.MatchText == "CHEWY PET SUPPLIES" && r.CategoryId == petCare.Id));
        }
    }

    [Fact]
    public async Task Apply_IsIdempotent_DoesNotDoubleCreateRulesOrRecategorize()
    {
        var accepted = new[]
        {
            new SmartCategorizationService.AcceptedMapping("NETFLIX", "Entertainment", IsNew: false, CreateRule: true)
        };

        await using (var db = NewDb())
        {
            var smart = new SmartCategorizationService(db, new CategorizationService(db));
            var first = await smart.ApplyAsync(UserId, accepted);
            Assert.Equal(3, first.TransactionsUpdated);
            Assert.Equal(1, first.RulesCreated);
        }

        // Second run: Netflix rows are already categorized, so nothing left to update, no dup rule.
        await using (var db = NewDb())
        {
            var smart = new SmartCategorizationService(db, new CategorizationService(db));
            var second = await smart.ApplyAsync(UserId, accepted);
            Assert.Equal(0, second.TransactionsUpdated);
            Assert.Equal(0, second.RulesCreated);
        }
    }

    public void Dispose() => _connection.Dispose();
}
