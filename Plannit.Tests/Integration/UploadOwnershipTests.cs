using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Tests.Integration;

/// <summary>
/// Secure-behaviour regression tests for audit P2-04: a staged upload can only be previewed,
/// confirmed or consumed by the user, account and step it was staged for, exactly once, before it
/// expires. Each "B cannot" test uses B's own valid antiforgery token, B's own account and A's exact
/// file id, which is the scenario the audit's characterization test showed succeeding.
/// </summary>
public class UploadOwnershipTests : IDisposable
{
    private const string Marker = "AUDIT_PRIVATE_A";

    private readonly PlannitWebAppFactory _factory = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
        _factory.Dispose();
    }

    private sealed record Actor(HttpClient Client, string UserId, int AccountId, int SecondAccountId);

    private async Task<Actor> NewActorAsync(string email)
    {
        var client = _factory.CreateClientNoRedirect();
        await HttpTestHelpers.RegisterAsync(client, email);
        using var scope = _factory.Services.CreateScope();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(user.Id);
        var a = new Account { UserId = user.Id, Name = "Main", Type = AccountType.Checking };
        var b = new Account { UserId = user.Id, Name = "Other", Type = AccountType.Checking };
        db.Accounts.AddRange(a, b);
        await db.SaveChangesAsync();
        return new Actor(client, user.Id, a.Id, b.Id);
    }

    private async Task<string> UploadAsync(Actor who, string fileName, string content, bool positions = false, int? accountId = null)
    {
        var page = await who.Client.GetStringAsync("/Transactions/Import");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpTestHelpers.ExtractAntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent((accountId ?? who.AccountId).ToString()), "AccountId" },
            { new StringContent(positions ? "true" : "false"), "PositionsStatement" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "Files", fileName }
        };
        var response = await who.Client.PostAsync("/Transactions/Import", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = HttpTestHelpers.ExtractHiddenField(await response.Content.ReadAsStringAsync(), "TempFileId");
        _files.AddRange(Directory.GetFiles(TempDir, id + ".*"));
        return id;
    }

    private string TempDir =>
        Path.Combine(_factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "TempUploads");

    private Task<string> UploadCsvAsync(Actor who, int? accountId = null) =>
        UploadAsync(who, "statement.csv", $"Date,Description,Amount\n01/15/2026,{Marker},-4.50\n", accountId: accountId);

    private Task<string> UploadPositionsAsync(Actor who) =>
        UploadAsync(who, "positions.csv", "Symbol,Current value,Quantity\nVTI,\"$1,234.56\",10\n", positions: true);

    private async Task<HttpResponseMessage> ConfirmCsvAsync(Actor who, string tempId, int? accountId = null)
    {
        var page = await who.Client.GetStringAsync("/Transactions/Import");
        return await who.Client.PostAsync("/Transactions/ConfirmImport", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["AccountId"] = (accountId ?? who.AccountId).ToString(),
            ["AccountName"] = "x",
            ["FileName"] = "statement.csv",
            ["TempFileId"] = tempId,
            ["DateColumn"] = "Date",
            ["DateFormat"] = "MM/dd/yyyy",
            ["DescriptionColumn"] = "Description",
            ["AmountColumn"] = "Amount"
        }));
    }

    private async Task<HttpResponseMessage> ConfirmSnapshotAsync(Actor who, string tempId, string sourceType, int? accountId = null)
    {
        var page = await who.Client.GetStringAsync("/Transactions/Import");
        return await who.Client.PostAsync("/Transactions/ConfirmSnapshotImport", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
            ["AccountId"] = (accountId ?? who.AccountId).ToString(),
            ["AccountName"] = "x",
            ["FileName"] = "f",
            ["TempFileId"] = tempId,
            ["TempFileExtension"] = sourceType == "PdfStatement" ? ".pdf" : ".csv",
            ["SourceType"] = sourceType,
            ["AsOfDate"] = "2026-01-31",
            ["Balance"] = "1234.56"
        }));
    }

    /// <summary>Asserts the response is the "upload expired" redirect back to the upload screen.</summary>
    private static void AssertRejected(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected a redirect to the upload screen, got {(int)response.StatusCode}.");
        Assert.Contains("/Transactions/Import", response.Headers.Location!.ToString());
    }

    private async Task<int> CountTransactionsAsync(Actor who, string? description = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        return await db.Transactions.CountAsync(t => description == null || t.Description == description);
    }

    private async Task<int> CountSnapshotsAsync(Actor who, decimal balance)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        return await db.BalanceSnapshots.CountAsync(s => s.Balance == balance);
    }

    private async Task<TempUpload> RowAsync(Actor who, string tempId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        return await db.TempUploads.SingleAsync(u => u.Id == Guid.Parse(tempId));
    }

    private async Task MutateRowAsync(string tempId, Action<TempUpload> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.TempUploads.IgnoreQueryFilters().SingleAsync(u => u.Id == Guid.Parse(tempId));
        change(row);
        await db.SaveChangesAsync();
    }

    /// <summary>Stages a PDF upload directly (a real PDF is not needed to exercise ownership).</summary>
    private async Task<string> SeedPdfAsync(Actor who)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, id.ToString("D") + ".pdf");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("%PDF-1.4 audit"));
        _files.Add(path);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(who.UserId);
        db.TempUploads.Add(new TempUpload
        {
            Id = id, UserId = who.UserId, AccountId = who.AccountId, Kind = "PdfStatement", Extension = ".pdf",
            CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        return id.ToString("D");
    }

    // ---------------------------------------------------------------------------------------
    // CSV flow
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Csv_OtherUser_CannotImport_WithExactIdAndOwnValidToken_AndOwnerStillCan()
    {
        var a = await NewActorAsync("upload-a@example.invalid");
        var b = await NewActorAsync("upload-b@example.invalid");
        var id = await UploadCsvAsync(a);

        AssertRejected(await ConfirmCsvAsync(b, id));

        Assert.Equal(0, await CountTransactionsAsync(b));
        Assert.Equal(0, await CountTransactionsAsync(a)); // A's pending import was not consumed either
        var owner = await ConfirmCsvAsync(a, id);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(1, await CountTransactionsAsync(a, Marker));
        Assert.Equal(0, await CountTransactionsAsync(b));
    }

    [Fact]
    public async Task Csv_OtherUser_CannotPreviewOwnersFile()
    {
        var a = await NewActorAsync("preview-a@example.invalid");
        var b = await NewActorAsync("preview-b@example.invalid");
        var id = await UploadCsvAsync(a);

        // Omitting the required Date column makes the controller re-render MapColumns with a preview
        // of the staged file. For B that preview must stay empty.
        async Task<string> PreviewAsync(Actor who, int accountId)
        {
            var page = await who.Client.GetStringAsync("/Transactions/Import");
            var response = await who.Client.PostAsync("/Transactions/ConfirmImport", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = HttpTestHelpers.ExtractAntiforgeryToken(page),
                ["AccountId"] = accountId.ToString(), ["AccountName"] = "x", ["FileName"] = "statement.csv",
                ["TempFileId"] = id, ["DateFormat"] = "MM/dd/yyyy", ["DescriptionColumn"] = "Description", ["AmountColumn"] = "Amount"
            }));
            return await response.Content.ReadAsStringAsync();
        }

        Assert.DoesNotContain(Marker, await PreviewAsync(b, b.AccountId));
        Assert.Contains(Marker, await PreviewAsync(a, a.AccountId)); // positive control: the owner does see it
    }

    [Fact]
    public async Task Csv_SameUser_CannotUseUploadForADifferentAccount()
    {
        var a = await NewActorAsync("account-bound@example.invalid");
        var id = await UploadCsvAsync(a);

        AssertRejected(await ConfirmCsvAsync(a, id, accountId: a.SecondAccountId));

        Assert.Equal(0, await CountTransactionsAsync(a));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmCsvAsync(a, id)).StatusCode);
    }

    [Fact]
    public async Task Csv_Replay_IsRejected_AndFileIsGone()
    {
        var a = await NewActorAsync("replay@example.invalid");
        var id = await UploadCsvAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmCsvAsync(a, id)).StatusCode);
        AssertRejected(await ConfirmCsvAsync(a, id));

        Assert.Equal(1, await CountTransactionsAsync(a, Marker));
        Assert.NotNull((await RowAsync(a, id)).ConsumedUtc);
        Assert.Empty(Directory.GetFiles(TempDir, id + ".*"));
    }

    [Fact]
    public async Task Csv_ConcurrentConfirms_ImportOnce()
    {
        var a = await NewActorAsync("race@example.invalid");
        var id = await UploadCsvAsync(a);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConfirmCsvAsync(a, id)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, await CountTransactionsAsync(a, Marker));
    }

    [Fact]
    public async Task Csv_ExpiredUpload_IsRejected()
    {
        var a = await NewActorAsync("expired@example.invalid");
        var id = await UploadCsvAsync(a);
        await MutateRowAsync(id, r => r.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        AssertRejected(await ConfirmCsvAsync(a, id));

        Assert.Equal(0, await CountTransactionsAsync(a));
    }

    [Fact]
    public async Task Csv_UploadStagedForAnotherStep_IsRejected()
    {
        var a = await NewActorAsync("wrong-step@example.invalid");
        var positionsId = await UploadPositionsAsync(a);
        var csvId = await UploadCsvAsync(a);

        AssertRejected(await ConfirmCsvAsync(a, positionsId));            // positions file through the CSV mapper
        AssertRejected(await ConfirmSnapshotAsync(a, csvId, "PositionsCsv")); // CSV map file through the snapshot step
        AssertRejected(await ConfirmSnapshotAsync(a, csvId, "PdfStatement"));

        Assert.Equal(0, await CountTransactionsAsync(a));
        Assert.Equal(0, await CountSnapshotsAsync(a, 1234.56m));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../appsettings")]
    [InlineData("..\\..\\plannit.db")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task TamperedReferences_FailSafely(string tempId)
    {
        var a = await NewActorAsync($"tamper-{Guid.NewGuid():N}@example.invalid");

        AssertRejected(await ConfirmCsvAsync(a, tempId));
        AssertRejected(await ConfirmSnapshotAsync(a, tempId, "PositionsCsv"));
        AssertRejected(await ConfirmSnapshotAsync(a, tempId, "PdfStatement"));
        Assert.Equal(0, await CountTransactionsAsync(a));
    }

    [Fact]
    public async Task MissingReference_ChangesNothing()
    {
        // A blank id fails model validation (the view is re-rendered); either way nothing is written.
        var a = await NewActorAsync("blank-ref@example.invalid");

        await ConfirmCsvAsync(a, "");
        await ConfirmSnapshotAsync(a, "", "PositionsCsv");

        Assert.Equal(0, await CountTransactionsAsync(a));
        Assert.Equal(0, await CountSnapshotsAsync(a, 1234.56m));
    }

    [Fact]
    public async Task ForgedFileOnDisk_WithoutRegistryRow_IsRejected()
    {
        // Dropping a file at a guessable path is not enough: no registry row, no access.
        var a = await NewActorAsync("forged@example.invalid");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, id.ToString("D") + ".csv");
        await File.WriteAllTextAsync(path, $"Date,Description,Amount\n01/15/2026,{Marker},-4.50\n");
        _files.Add(path);

        AssertRejected(await ConfirmCsvAsync(a, id.ToString("D")));

        Assert.Equal(0, await CountTransactionsAsync(a));
    }

    // ---------------------------------------------------------------------------------------
    // Positions and PDF (snapshot) flow
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Positions_OtherUser_CannotConfirm_AndOwnerStillCan()
    {
        var a = await NewActorAsync("pos-a@example.invalid");
        var b = await NewActorAsync("pos-b@example.invalid");
        var id = await UploadPositionsAsync(a);

        AssertRejected(await ConfirmSnapshotAsync(b, id, "PositionsCsv"));

        Assert.Equal(0, await CountSnapshotsAsync(b, 1234.56m));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmSnapshotAsync(a, id, "PositionsCsv")).StatusCode);
        Assert.Equal(1, await CountSnapshotsAsync(a, 1234.56m));
    }

    [Fact]
    public async Task Positions_Replay_IsRejected()
    {
        var a = await NewActorAsync("pos-replay@example.invalid");
        var id = await UploadPositionsAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmSnapshotAsync(a, id, "PositionsCsv")).StatusCode);
        AssertRejected(await ConfirmSnapshotAsync(a, id, "PositionsCsv"));
    }

    [Fact]
    public async Task Pdf_OtherUser_CannotConfirm_AndOwnerStillCan()
    {
        var a = await NewActorAsync("pdf-a@example.invalid");
        var b = await NewActorAsync("pdf-b@example.invalid");
        var id = await SeedPdfAsync(a);

        AssertRejected(await ConfirmSnapshotAsync(b, id, "PdfStatement"));

        Assert.Equal(0, await CountSnapshotsAsync(b, 1234.56m));
        Assert.Equal(HttpStatusCode.OK, (await ConfirmSnapshotAsync(a, id, "PdfStatement")).StatusCode);
        Assert.Equal(1, await CountSnapshotsAsync(a, 1234.56m));
        AssertRejected(await ConfirmSnapshotAsync(a, id, "PdfStatement")); // replay
    }

    [Fact]
    public async Task Pdf_ExpiredUpload_IsRejected_AndWritesNoSnapshot()
    {
        var a = await NewActorAsync("pdf-expired@example.invalid");
        var id = await SeedPdfAsync(a);
        await MutateRowAsync(id, r => r.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        AssertRejected(await ConfirmSnapshotAsync(a, id, "PdfStatement"));

        Assert.Equal(0, await CountSnapshotsAsync(a, 1234.56m));
    }

    // ---------------------------------------------------------------------------------------
    // Registry and cleanup
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_IsRegisteredForTheUploader_WithBoundedLifetime()
    {
        var a = await NewActorAsync("registry@example.invalid");
        var id = await UploadCsvAsync(a);

        var row = await RowAsync(a, id);

        Assert.Equal(a.UserId, row.UserId);
        Assert.Equal(a.AccountId, row.AccountId);
        Assert.Equal("CsvMap", row.Kind);
        Assert.Equal(".csv", row.Extension);
        Assert.Null(row.ConsumedUtc);
        Assert.InRange(row.ExpiresUtc - row.CreatedUtc, TimeSpan.Zero, ImportWorkflowService.UploadLifetime);
    }

    [Fact]
    public async Task Registry_IsTenantScoped()
    {
        var a = await NewActorAsync("scope-a@example.invalid");
        var b = await NewActorAsync("scope-b@example.invalid");
        var id = await UploadCsvAsync(a);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(b.UserId);
        Assert.False(await db.TempUploads.AnyAsync(u => u.Id == Guid.Parse(id)));

        var anonymous = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await anonymous.TempUploads.AnyAsync());
    }

    [Fact]
    public async Task MaintenanceSweep_RemovesExpiredAndConsumedUploads_ButKeepsLiveOnes()
    {
        var a = await NewActorAsync("sweep@example.invalid");
        var expired = await UploadCsvAsync(a);
        var consumed = await UploadCsvAsync(a);
        var live = await UploadCsvAsync(a);
        await MutateRowAsync(expired, r => r.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));
        await MutateRowAsync(consumed, r => r.ConsumedUtc = DateTime.UtcNow);

        var maintenance = _factory.Services.GetServices<IHostedService>().OfType<MaintenanceBackgroundService>().Single();
        var sweep = typeof(MaintenanceBackgroundService).GetMethod("CleanupTempUploadsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)sweep.Invoke(maintenance, null)!;

        Assert.Empty(Directory.GetFiles(TempDir, expired + ".*"));
        Assert.Empty(Directory.GetFiles(TempDir, consumed + ".*"));
        Assert.Single(Directory.GetFiles(TempDir, live + ".*"));
        Assert.Null((await RowAsync(a, live)).ConsumedUtc);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(new[] { Guid.Parse(live) }, await db.TempUploads.IgnoreQueryFilters().Select(u => u.Id).ToArrayAsync());
    }
}
