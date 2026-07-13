using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Tests.Integration;

/// <summary>
/// End-to-end HTTP coverage of the import workflow (now owned by ImportWorkflowService),
/// auth redirects, and multi-tenant isolation over the real request pipeline.
/// All tests share one factory (rate limiter disabled) and run sequentially.
/// </summary>
public class ImportFlowIntegrationTests : IClassFixture<PlannitWebAppFactory>
{
    private readonly PlannitWebAppFactory _factory;

    public ImportFlowIntegrationTests(PlannitWebAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> GetUserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user!.Id;
    }

    private async Task<int> SeedAccountAsync(string userId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(userId);
        var account = new Account { UserId = userId, Name = name, Type = AccountType.Checking };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<int> SeedTransactionAsync(string userId, int accountId, string description, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(userId);
        var txn = new Transaction
        {
            AccountId = accountId,
            Date = new DateOnly(2026, 1, 5),
            Amount = amount,
            Description = description,
            OriginalDescription = description
        };
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();
        return txn.Id;
    }

    private async Task<int> CountTransactionsAsync(string userId, int accountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(userId);
        return await db.Transactions.CountAsync(t => t.AccountId == accountId);
    }

    [Fact]
    public async Task Transactions_Unauthenticated_RedirectsToLogin()
    {
        var client = _factory.CreateClientNoRedirect();

        var resp = await client.GetAsync("/Transactions");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Identity/Account/Login", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ImportCsv_UploadMapConfirm_ImportsTransactions()
    {
        var email = $"import-{Guid.NewGuid():N}@test.local";
        var client = _factory.CreateClient();
        await HttpTestHelpers.RegisterAsync(client, email);
        var userId = await GetUserIdAsync(email);
        var accountId = await SeedAccountAsync(userId, "Import Checking");

        // Step 1 — upload a CSV with no saved profile → the MapColumns screen is returned.
        var importGet = await client.GetAsync("/Transactions/Import");
        var importToken = HttpTestHelpers.ExtractAntiforgeryToken(await importGet.Content.ReadAsStringAsync());

        const string csv = "Date,Description,Amount\n01/15/2026,Coffee Shop,-4.50\n01/16/2026,Paycheck,1000.00\n";
        using var upload = new MultipartFormDataContent
        {
            { new StringContent(importToken), "__RequestVerificationToken" },
            { new StringContent(accountId.ToString()), "AccountId" }
        };
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        upload.Add(fileContent, "Files", "statement.csv");

        var mapResp = await client.PostAsync("/Transactions/Import", upload);
        Assert.Equal(HttpStatusCode.OK, mapResp.StatusCode);
        var mapHtml = await mapResp.Content.ReadAsStringAsync();
        Assert.Contains("Map Columns", mapHtml);

        var tempFileId = HttpTestHelpers.ExtractHiddenField(mapHtml, "TempFileId");
        var confirmToken = HttpTestHelpers.ExtractAntiforgeryToken(mapHtml);
        Assert.False(string.IsNullOrEmpty(tempFileId));

        // Step 2 — confirm the mapping → import runs, result page renders.
        var confirmForm = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = confirmToken,
            ["AccountId"] = accountId.ToString(),
            ["AccountName"] = "Import Checking",
            ["FileName"] = "statement.csv",
            ["TempFileId"] = tempFileId,
            ["DateColumn"] = "Date",
            ["DateFormat"] = "MM/dd/yyyy",
            ["DescriptionColumn"] = "Description",
            ["AmountColumn"] = "Amount",
            ["InvertAmounts"] = "false"
        };

        var confirmResp = await client.PostAsync("/Transactions/ConfirmImport", new FormUrlEncodedContent(confirmForm));
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        var resultHtml = await confirmResp.Content.ReadAsStringAsync();
        Assert.Contains("Import Checking", resultHtml);

        // Both rows landed in the account.
        Assert.Equal(2, await CountTransactionsAsync(userId, accountId));
    }

    [Fact]
    public async Task ImportCsv_WithSavedProfile_ImportsDirectlyToResult()
    {
        var email = $"profile-{Guid.NewGuid():N}@test.local";
        var client = _factory.CreateClient();
        await HttpTestHelpers.RegisterAsync(client, email);
        var userId = await GetUserIdAsync(email);
        var accountId = await SeedAccountAsync(userId, "Profile Checking");

        // First import establishes the saved column profile via the map screen.
        await RunFullCsvImportAsync(client, accountId, "Profile Checking",
            "Date,Description,Amount\n02/01/2026,Store One,-10.00\n");

        // Second import of the same account skips MapColumns and goes straight to the result page.
        var importGet = await client.GetAsync("/Transactions/Import");
        var importToken = HttpTestHelpers.ExtractAntiforgeryToken(await importGet.Content.ReadAsStringAsync());

        const string csv = "Date,Description,Amount\n02/05/2026,Store Two,-20.00\n02/06/2026,Store Three,-30.00\n";
        using var upload = new MultipartFormDataContent
        {
            { new StringContent(importToken), "__RequestVerificationToken" },
            { new StringContent(accountId.ToString()), "AccountId" }
        };
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        upload.Add(fileContent, "Files", "second.csv");

        var resp = await client.PostAsync("/Transactions/Import", upload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        // Went straight to the result page — no mapping step this time.
        Assert.DoesNotContain("Map Columns", html);

        Assert.Equal(3, await CountTransactionsAsync(userId, accountId));
    }

    [Fact]
    public async Task Tenancy_UserB_CannotSee_UserA_TransactionsOverHttp()
    {
        var emailA = $"tenant-a-{Guid.NewGuid():N}@test.local";
        var emailB = $"tenant-b-{Guid.NewGuid():N}@test.local";

        var clientA = _factory.CreateClient();
        await HttpTestHelpers.RegisterAsync(clientA, emailA);
        var userAId = await GetUserIdAsync(emailA);
        var accountAId = await SeedAccountAsync(userAId, "A Checking");
        var txnAId = await SeedTransactionAsync(userAId, accountAId, "SecretStoreA", -55.00m);

        var clientB = _factory.CreateClient();
        await HttpTestHelpers.RegisterAsync(clientB, emailB);

        // B's transaction list must not leak A's data.
        var listResp = await clientB.GetAsync("/Transactions");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listHtml = await listResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SecretStoreA", listHtml);

        // B editing A's transaction by id must 404 (ownership enforced through the query filter).
        var editResp = await clientB.GetAsync($"/Transactions/Edit/{txnAId}");
        Assert.Equal(HttpStatusCode.NotFound, editResp.StatusCode);
    }

    // Drives a complete upload → map → confirm cycle; used to prime a saved profile.
    private static async Task RunFullCsvImportAsync(HttpClient client, int accountId, string accountName, string csv)
    {
        var importGet = await client.GetAsync("/Transactions/Import");
        var importToken = HttpTestHelpers.ExtractAntiforgeryToken(await importGet.Content.ReadAsStringAsync());

        using var upload = new MultipartFormDataContent
        {
            { new StringContent(importToken), "__RequestVerificationToken" },
            { new StringContent(accountId.ToString()), "AccountId" }
        };
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        upload.Add(fileContent, "Files", "first.csv");

        var mapResp = await client.PostAsync("/Transactions/Import", upload);
        var mapHtml = await mapResp.Content.ReadAsStringAsync();
        var tempFileId = HttpTestHelpers.ExtractHiddenField(mapHtml, "TempFileId");
        var confirmToken = HttpTestHelpers.ExtractAntiforgeryToken(mapHtml);

        var confirmForm = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = confirmToken,
            ["AccountId"] = accountId.ToString(),
            ["AccountName"] = accountName,
            ["FileName"] = "first.csv",
            ["TempFileId"] = tempFileId,
            ["DateColumn"] = "Date",
            ["DateFormat"] = "MM/dd/yyyy",
            ["DescriptionColumn"] = "Description",
            ["AmountColumn"] = "Amount",
            ["InvertAmounts"] = "false"
        };
        await client.PostAsync("/Transactions/ConfirmImport", new FormUrlEncodedContent(confirmForm));
    }
}
