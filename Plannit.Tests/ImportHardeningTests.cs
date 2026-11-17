using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests;

/// <summary>
/// Uploaded statements are untrusted. These pin two properties: reading a hostile file takes time
/// proportional to its size (the old patterns took minutes for inputs of this size and would not
/// finish at the 10 MB upload limit), and what an import or a form can store is bounded.
/// </summary>
public class ImportHardeningTests : IDisposable
{
    // Generous: each case below runs in well under a second with linear matching, and took from
    // tens of seconds to hours with the backtracking patterns.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    private const string UserId = "limits-user";
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly int _accountId;

    public ImportHardeningTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var db = new ApplicationDbContext(_options);
        db.Database.EnsureCreated();
        db.Users.Add(new IdentityUser { Id = UserId, UserName = "limits@test.com", Email = "limits@test.com" });
        var account = new Account { UserId = UserId, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.Add(account);
        db.SaveChanges();
        _accountId = account.Id;
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(_options);
        db.SetCurrentUser(UserId);
        return db;
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static TimeSpan Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed;
    }

    // ===== Linear-time parsing =====

    [Fact]
    public void OfxTagValue_PaddedWithWhitespace_IsReadInLinearTime()
    {
        var block = "<NAME>a" + new string(' ', 400_000) + "b";

        string? value = null;
        var elapsed = Time(() => value = OfxImportService.GetTagValue(block, "NAME"));

        Assert.True(elapsed < Budget, $"took {elapsed}");
        Assert.StartsWith("a", value);
        Assert.EndsWith("b", value);
    }

    [Fact]
    public void OfxTransactions_ThousandsOfUnclosedBlocks_AreReadInLinearTime()
    {
        var content = string.Concat(Enumerable.Repeat("<STMTTRN>", 100_000));

        var count = 0;
        var elapsed = Time(() => count = OfxImportService.ParseTransactions(content).Count);

        Assert.True(elapsed < Budget, $"took {elapsed}");
        Assert.Equal(100_000, count);
    }

    [Fact]
    public void OfxLedgerBalance_UnclosedBlocks_AreReadInLinearTime()
    {
        var content = string.Concat(Enumerable.Repeat("<LEDGERBAL>", 100_000));

        var elapsed = Time(() => OfxImportService.ParseLedgerBalance(content));

        Assert.True(elapsed < Budget, $"took {elapsed}");
    }

    [Fact]
    public void OfxParsing_StillReadsAnOrdinaryStatement()
    {
        const string content = """
            <OFX><BANKTRANLIST>
            <STMTTRN>
            <TRNTYPE>DEBIT
            <DTPOSTED>20260105120000[0:GMT]
            <TRNAMT>-4.50
            <FITID>abc123
            <NAME>Coffee Shop
            <MEMO>Card purchase
            </STMTTRN>
            <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260106</DTPOSTED><TRNAMT>1,000.00</TRNAMT><FITID>abc124</FITID><NAME>Paycheck</NAME></STMTTRN>
            </BANKTRANLIST>
            <LEDGERBAL><BALAMT>995.50<DTASOF>20260106</LEDGERBAL></OFX>
            """;

        var transactions = OfxImportService.ParseTransactions(content);
        var (balance, asOf) = OfxImportService.ParseLedgerBalance(content);

        Assert.Equal(2, transactions.Count);
        Assert.Equal("Coffee Shop", transactions[0].Name);
        Assert.Equal("Card purchase", transactions[0].Memo);
        Assert.Equal(-4.50m, transactions[0].Amount);
        Assert.Equal(new DateOnly(2026, 1, 5), transactions[0].Date);
        Assert.Equal("abc123", transactions[0].FitId);
        Assert.Equal("Paycheck", transactions[1].Name);
        Assert.Equal(1000m, transactions[1].Amount);
        Assert.Equal(995.50m, balance);
        Assert.Equal(new DateOnly(2026, 1, 6), asOf);
    }

    [Theory]
    [InlineData("to")]
    [InlineData("yourbalanceon")]
    [InlineData("endingbalance$1,")]
    public void PdfText_RepeatedNearMatches_AreReadInLinearTime(string fragment)
    {
        var text = string.Concat(Enumerable.Repeat(fragment, 1_000_000 / fragment.Length));

        var elapsed = Time(() => PdfStatementService.ParseFromText(text));

        Assert.True(elapsed < Budget, $"took {elapsed}");
    }

    [Fact]
    public void PdfText_StillFindsBalanceAndDate()
    {
        var preview = PdfStatementService.ParseFromText("Statement period June 1, 2026 to June 30, 2026\nEnding balance $1,234.56");

        Assert.Equal(1234.56m, preview.Balance);
        Assert.Equal(new DateOnly(2026, 6, 30), preview.AsOfDate);
    }

    [Fact]
    public void PositionsCsv_PaddedDateCell_IsReadInLinearTime()
    {
        var cell = string.Concat(Enumerable.Repeat("Date" + new string(' ', 2_000), 150));
        var csv = "Symbol,Current value\nVTI,\"$100.00\"\n\"" + cell + "\",\n\"Date downloaded Jun-30-2026\",\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        PositionsImportPreview? preview = null;
        var elapsed = Time(() => preview = new PositionsCsvImportService().Parse(stream));

        Assert.True(elapsed < Budget, $"took {elapsed}");
        Assert.True(preview!.Success);
        Assert.Equal(new DateOnly(2026, 6, 30), preview.AsOfDate);
    }

    // ===== What an import may store =====

    private static MemoryStream Csv(IEnumerable<string> rows) =>
        new(Encoding.UTF8.GetBytes("Date,Description,Amount\n" + string.Join("\n", rows) + "\n"));

    private Task<Models.ViewModels.ImportResultViewModel> ImportCsvAsync(ApplicationDbContext db, Stream stream, IConfiguration? config = null) =>
        new CsvImportService(db, config).ImportAsync(stream, _accountId, "f.csv", "Date", "MM/dd/yyyy", "Amount", null, null, "Description");

    [Fact]
    public async Task CsvImport_ShortensOversizedDescriptions()
    {
        using var db = NewDb();
        using var stream = Csv([$"01/05/2026,{new string('x', 50_000)},-1.00"]);

        var result = await ImportCsvAsync(db, stream);

        Assert.Equal(1, result.ImportedCount);
        var saved = await db.Transactions.SingleAsync();
        Assert.Equal(ImportLimits.MaxDescriptionLength, saved.Description.Length);
        Assert.Equal(ImportLimits.MaxDescriptionLength, saved.OriginalDescription!.Length);
    }

    [Fact]
    public async Task CsvImport_StopsAtThePerFileRowLimit_AndSaysSo()
    {
        using var db = NewDb();
        using var stream = Csv(Enumerable.Range(1, 10).Select(i => $"01/05/2026,Row {i},-{i}.00"));

        var result = await ImportCsvAsync(db, stream, Config(("Import:MaxRowsPerFile", "3")));

        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(3, await db.Transactions.CountAsync());
        Assert.Contains(result.Errors, e => e.Message.Contains("more than 3 rows"));
    }

    [Fact]
    public async Task CsvImport_StopsAtThePerUserTransactionLimit_AndSaysSo()
    {
        using var db = NewDb();
        db.Transactions.Add(new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -1, Description = "existing" });
        await db.SaveChangesAsync();
        using var stream = Csv(Enumerable.Range(1, 10).Select(i => $"01/05/2026,Row {i},-{i}.00"));

        var result = await ImportCsvAsync(db, stream, Config(("Import:MaxTransactionsPerUser", "4")));

        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(4, await db.Transactions.CountAsync());
        Assert.Contains(result.Errors, e => e.Message.Contains("limit of 4 transactions"));
    }

    [Fact]
    public async Task CsvImport_AtTheLimit_ImportsNothingMore()
    {
        using var db = NewDb();
        db.Transactions.Add(new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -1, Description = "existing" });
        await db.SaveChangesAsync();
        using var stream = Csv(["01/05/2026,One more,-1.00"]);

        var result = await ImportCsvAsync(db, stream, Config(("Import:MaxTransactionsPerUser", "1")));

        Assert.Equal(0, result.ImportedCount);
        Assert.Equal(1, await db.Transactions.CountAsync());
    }

    private static string Ofx(int count, string? name = null) =>
        string.Concat(Enumerable.Range(1, count).Select(i =>
            $"<STMTTRN><DTPOSTED>20260105<TRNAMT>-{i}.00<FITID>id{i}<NAME>{name ?? $"Row {i}"}</STMTTRN>\n"));

    private Task<Models.ViewModels.ImportResultViewModel> ImportOfxAsync(ApplicationDbContext db, string content, IConfiguration? config = null)
    {
        var service = new OfxImportService(db, new SnapshotImportService(new AccountService(db)), config);
        return service.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), _accountId, "f.ofx");
    }

    [Fact]
    public async Task OfxImport_ShortensOversizedDescriptionsAndIds()
    {
        using var db = NewDb();
        var content = $"<STMTTRN><DTPOSTED>20260105<TRNAMT>-1.00<FITID>{new string('7', 5_000)}<NAME>{new string('x', 50_000)}</STMTTRN>";

        var result = await ImportOfxAsync(db, content);

        Assert.Equal(1, result.ImportedCount);
        var saved = await db.Transactions.SingleAsync();
        Assert.Equal(ImportLimits.MaxDescriptionLength, saved.Description.Length);
        Assert.Equal(ImportLimits.MaxBankIdLength, saved.OfxFitId!.Length);
    }

    [Fact]
    public async Task OfxImport_StopsAtThePerFileRowLimit_AndSaysSo()
    {
        using var db = NewDb();

        var result = await ImportOfxAsync(db, Ofx(10), Config(("Import:MaxRowsPerFile", "3")));

        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(3, await db.Transactions.CountAsync());
        Assert.Contains(result.Errors, e => e.Message.Contains("more than 3 rows"));
    }

    [Fact]
    public async Task OfxImport_StopsAtThePerUserTransactionLimit_AndSaysSo()
    {
        using var db = NewDb();

        var result = await ImportOfxAsync(db, Ofx(10), Config(("Import:MaxTransactionsPerUser", "4")));

        Assert.Equal(4, result.ImportedCount);
        Assert.Equal(4, await db.Transactions.CountAsync());
        Assert.Contains(result.Errors, e => e.Message.Contains("limit of 4 transactions"));
    }

    [Fact]
    public async Task TransactionLimit_CountsOnlyTheCurrentUsersRows()
    {
        using (var seed = new ApplicationDbContext(_options))
        {
            seed.Users.Add(new IdentityUser { Id = "other", UserName = "other@test.com" });
            var other = new Account { UserId = "other", Name = "Other", Type = AccountType.Checking };
            seed.Accounts.Add(other);
            seed.SaveChanges();
            seed.Transactions.AddRange(Enumerable.Range(1, 5).Select(i =>
                new Transaction { AccountId = other.Id, Date = new DateOnly(2026, 1, 1), Amount = -i, Description = "theirs" }));
            seed.SaveChanges();
        }

        using var db = NewDb();
        var result = await ImportOfxAsync(db, Ofx(3), Config(("Import:MaxTransactionsPerUser", "3")));

        Assert.Equal(3, result.ImportedCount);
    }

    // ===== What any write may store =====

    [Fact]
    public async Task OversizedText_IsShortenedOnSave_ForAddsAndEdits()
    {
        var huge = new string('n', 1_000_000);
        int id;
        using (var db = NewDb())
        {
            var txn = new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -1, Description = "d", Notes = huge };
            db.Transactions.Add(txn);
            db.Categories.Add(new Category { UserId = UserId, Name = huge });
            await db.SaveChangesAsync();
            id = txn.Id;
        }

        using (var db = NewDb())
        {
            Assert.Equal(ApplicationDbContext.MaxStoredTextLength, (await db.Transactions.SingleAsync(t => t.Id == id)).Notes!.Length);
            Assert.Equal(ApplicationDbContext.MaxStoredTextLength, (await db.Categories.SingleAsync()).Name.Length);

            await new DataManagementService(db).UpdateTransactionNotesAsync(id, "short");
            await new DataManagementService(db).UpdateTransactionNotesAsync(id, huge);
        }

        using (var db = NewDb())
            Assert.Equal(ApplicationDbContext.MaxStoredTextLength, (await db.Transactions.SingleAsync(t => t.Id == id)).Notes!.Length);
    }

    [Fact]
    public async Task TextWithinTheLimit_IsStoredUnchanged()
    {
        var notes = new string('n', ApplicationDbContext.MaxStoredTextLength);
        using (var db = NewDb())
        {
            db.Transactions.Add(new Transaction { AccountId = _accountId, Date = new DateOnly(2026, 1, 1), Amount = -1, Description = "d", Notes = notes });
            await db.SaveChangesAsync();
        }

        using var read = NewDb();
        Assert.Equal(notes, (await read.Transactions.SingleAsync()).Notes);
    }

    [Fact]
    public async Task EncryptedValuesAndIdentityRows_AreNeverShortened()
    {
        var blob = new string('k', 10_000);
        using (var db = NewDb())
        {
            db.AiSettings.Add(new AiSettings { UserId = UserId, ApiKeyProtected = blob });
            var user = await db.Users.SingleAsync(u => u.Id == UserId);
            user.PasswordHash = blob;
            await db.SaveChangesAsync();
        }

        using var read = NewDb();
        Assert.Equal(blob, (await read.AiSettings.SingleAsync()).ApiKeyProtected);
        Assert.Equal(blob, (await read.Users.SingleAsync(u => u.Id == UserId)).PasswordHash);
    }

    [Theory]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("ab\U0001F600cd", 3, "ab")] // never split a surrogate pair
    public void Clamp_ShortensWithoutSplittingCharacters(string input, int max, string expected) =>
        Assert.Equal(expected, ImportLimits.Clamp(input, max));
}
