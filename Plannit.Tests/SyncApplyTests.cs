using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Services.Sync;

namespace Plannit.Tests;

public class SyncApplyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private const string UserId = "user-1";
    private const string AccessUrl = "https://u:p@bridge.simplefin.org/simplefin";

    public SyncApplyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var seed = new ApplicationDbContext(_options);
        seed.Database.EnsureCreated();
        seed.Users.Add(new IdentityUser { Id = UserId, UserName = "u@test.com", Email = "u@test.com", NormalizedEmail = "U@TEST.COM" });
        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext CreateContext()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    private SyncService CreateService(ApplicationDbContext db, SimpleFinClient client)
    {
        var accountService = new AccountService(db);
        var snapshotImport = new SnapshotImportService(accountService);
        var categorization = new CategorizationService(db);
        return new SyncService(db, client, snapshotImport, categorization, _dataProtection, NullLogger<SyncService>.Instance);
    }

    private string Protect(string plain) =>
        _dataProtection.CreateProtector("Plannit.SyncConnection.AccessUrl").Protect(plain);

    /// <summary>Seeds a checking account, a connection, and a mapping linking the two.</summary>
    private async Task<(int AccountId, int ConnectionId)> SeedMappedConnectionAsync(AccountType type = AccountType.Checking)
    {
        using var db = CreateContext();
        var account = new Account { UserId = UserId, Name = "Checking", Type = type };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var conn = new SyncConnection { UserId = UserId, AccessUrlProtected = Protect(AccessUrl) };
        db.SyncConnections.Add(conn);
        await db.SaveChangesAsync();

        db.SyncAccountMappings.Add(new SyncAccountMapping
        {
            SyncConnectionId = conn.Id,
            ExternalAccountId = "EXT-1",
            AccountId = account.Id
        });
        await db.SaveChangesAsync();

        return (account.Id, conn.Id);
    }

    private static SimpleFinAccountSet BuildSet(string balance = "1000.00", params (string Id, string Amount, string Desc)[] txns)
    {
        var acct = new SimpleFinAccount
        {
            Id = "EXT-1",
            Name = "Everyday Checking",
            Balance = decimal.Parse(balance),
            BalanceDate = new DateOnly(2026, 7, 12)
        };
        foreach (var (id, amount, desc) in txns)
        {
            acct.Transactions.Add(new SimpleFinTransaction
            {
                Id = id,
                Posted = new DateOnly(2026, 7, 10),
                Amount = decimal.Parse(amount),
                Description = desc,
                Payee = desc
            });
        }
        return new SimpleFinAccountSet { Accounts = { acct } };
    }

    [Fact]
    public async Task SyncNow_ImportsTransactionsAndUpsertsSnapshot()
    {
        var (accountId, connId) = await SeedMappedConnectionAsync();
        var set = BuildSet("1523.44", ("T1", "-42.10", "UBER EATS"), ("T2", "3200.00", "PAYROLL"));

        using var db = CreateContext();
        var service = CreateService(db, new FakeSimpleFinClient(set));
        var result = await service.SyncNowAsync(connId);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(2, result.TransactionsImported);
        Assert.Equal(1, result.SnapshotsUpdated);

        using var verify = CreateContext();
        Assert.Equal(2, await verify.Transactions.CountAsync(t => t.AccountId == accountId));
        var snapshot = await verify.BalanceSnapshots.SingleAsync(s => s.AccountId == accountId);
        Assert.Equal(1523.44m, snapshot.Balance);
        Assert.Equal(new DateOnly(2026, 7, 12), snapshot.Date);

        // Transactions carry the SimpleFIN id as the strong dedup key.
        Assert.True(await verify.Transactions.AllAsync(t => t.OfxFitId != null));
    }

    [Fact]
    public async Task SyncNow_SecondRun_SkipsDuplicatesByExternalId()
    {
        var (accountId, connId) = await SeedMappedConnectionAsync();
        var set = BuildSet("1000.00", ("T1", "-42.10", "UBER EATS"), ("T2", "3200.00", "PAYROLL"));

        using (var db = CreateContext())
            await CreateService(db, new FakeSimpleFinClient(set)).SyncNowAsync(connId);

        // Same payload again — nothing new should be imported.
        using var db2 = CreateContext();
        var result = await CreateService(db2, new FakeSimpleFinClient(set)).SyncNowAsync(connId);

        Assert.Equal(0, result!.TransactionsImported);
        Assert.Equal(2, result.DuplicatesSkipped);

        using var verify = CreateContext();
        Assert.Equal(2, await verify.Transactions.CountAsync(t => t.AccountId == accountId));
    }

    [Fact]
    public async Task SyncNow_LiabilityBalance_NormalizedToPositiveSnapshot()
    {
        var (accountId, connId) = await SeedMappedConnectionAsync(AccountType.CreditCard);
        var set = BuildSet("-110.46", ("T1", "-9.99", "NETFLIX"));

        using var db = CreateContext();
        var result = await CreateService(db, new FakeSimpleFinClient(set)).SyncNowAsync(connId);

        Assert.Equal(1, result!.SnapshotsUpdated);
        using var verify = CreateContext();
        var snapshot = await verify.BalanceSnapshots.SingleAsync(s => s.AccountId == accountId);
        Assert.Equal(110.46m, snapshot.Balance); // liability sign normalized to positive
    }

    [Fact]
    public async Task SyncNow_UnmappedAccount_IsSkipped()
    {
        // Connection exists but the mapping's AccountId is null → account is left unlinked.
        using (var db = CreateContext())
        {
            var conn = new SyncConnection { UserId = UserId, AccessUrlProtected = Protect(AccessUrl) };
            db.SyncConnections.Add(conn);
            await db.SaveChangesAsync();
            db.SyncAccountMappings.Add(new SyncAccountMapping { SyncConnectionId = conn.Id, ExternalAccountId = "EXT-1", AccountId = null });
            await db.SaveChangesAsync();

            var set = BuildSet("500.00", ("T1", "-5.00", "COFFEE"));
            var result = await CreateService(db, new FakeSimpleFinClient(set)).SyncNowAsync(conn.Id);

            Assert.Equal(0, result!.TransactionsImported);
            Assert.Equal(1, result.UnmappedAccounts);
        }

        using var verify = CreateContext();
        Assert.Equal(0, await verify.Transactions.CountAsync());
    }

    [Fact]
    public async Task SyncNow_TokenRejected_MarksConnectionExpired()
    {
        var (_, connId) = await SeedMappedConnectionAsync();

        using var db = CreateContext();
        var result = await CreateService(db, new ThrowingSimpleFinClient()).SyncNowAsync(connId);

        Assert.NotNull(result);
        Assert.True(result!.TokenExpired);

        using var verify = CreateContext();
        var conn = await verify.SyncConnections.SingleAsync(c => c.Id == connId);
        Assert.Equal(SyncStatus.TokenExpired, conn.LastSyncStatus);
    }

    // ===== Fakes =====

    private sealed class FakeSimpleFinClient : SimpleFinClient
    {
        private readonly SimpleFinAccountSet _set;
        public FakeSimpleFinClient(SimpleFinAccountSet set) : base(new HttpClient(), NullLogger<SimpleFinClient>.Instance) => _set = set;
        public override Task<SimpleFinAccountSet> FetchAccountsAsync(string accessUrl, DateOnly? startDate = null, CancellationToken ct = default) => Task.FromResult(_set);
    }

    private sealed class ThrowingSimpleFinClient : SimpleFinClient
    {
        public ThrowingSimpleFinClient() : base(new HttpClient(), NullLogger<SimpleFinClient>.Instance) { }
        public override Task<SimpleFinAccountSet> FetchAccountsAsync(string accessUrl, DateOnly? startDate = null, CancellationToken ct = default) => throw new SimpleFinAuthException("expired");
    }
}
