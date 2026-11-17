using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Tests.Integration;

/// <summary>Request-level checks for the November security review fixes.</summary>
public class HardeningIntegrationTests : IClassFixture<PlannitWebAppFactory>
{
    private readonly PlannitWebAppFactory _factory;

    public HardeningIntegrationTests(PlannitWebAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string UserId, int AccountId)> SignedInUserAsync()
    {
        var email = $"hardening-{Guid.NewGuid():N}@test.local";
        var client = _factory.CreateClientNoRedirect();
        await HttpTestHelpers.RegisterAsync(client, email);

        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(user!.Id);
        var account = new Account { UserId = user.Id, Name = "Checking", Type = AccountType.Checking };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (client, user.Id, account.Id);
    }

    [Fact]
    public async Task HostileOfxUpload_IsAnsweredPromptly()
    {
        var (client, _, accountId) = await SignedInUserAsync();
        var token = HttpTestHelpers.ExtractAntiforgeryToken(await client.GetStringAsync("/Transactions/Import"));

        // One megabyte of whitespace inside a tag value, plus unclosed blocks: the shapes that made
        // the old parser's matching time explode.
        var ofx = "<STMTTRN><DTPOSTED>20260105<TRNAMT>-1.00<NAME>a" + new string(' ', 1_000_000) + "b\n"
                  + string.Concat(Enumerable.Repeat("<STMTTRN>", 20_000));
        using var upload = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(accountId.ToString()), "AccountId" }
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ofx));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        upload.Add(file, "Files", "statement.ofx");

        var sw = Stopwatch.StartNew();
        var response = await client.PostAsync("/Transactions/Import", upload);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task MergePage_KeepsTheCategoryNameOutOfInlineScript()
    {
        var (client, userId, _) = await SignedInUserAsync();
        const string name = "x');alert(document.domain);//";
        int categoryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SetCurrentUser(userId);
            var category = new Category { UserId = userId, Name = name };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
        }

        var html = await client.GetStringAsync($"/Categories/Merge/{categoryId}");

        // The browser decodes an attribute before running it, so check the decoded handlers.
        var handlers = System.Text.RegularExpressions.Regex.Matches(html, @"\son\w+=""([^""]*)""")
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .ToList();
        Assert.NotEmpty(handlers);
        Assert.DoesNotContain(handlers, h => h.Contains("alert(document.domain)"));
        Assert.Contains(name, WebUtility.HtmlDecode(html)); // still shown as text
    }

    [Fact]
    public async Task AddSnapshot_WithAnExternalReturnUrl_StaysOnThisSite()
    {
        var (client, _, accountId) = await SignedInUserAsync();
        var token = HttpTestHelpers.ExtractAntiforgeryToken(await client.GetStringAsync($"/Accounts/Details/{accountId}"));

        var response = await client.PostAsync("/Accounts/AddSnapshot?returnUrl=https%3A%2F%2Fevil.example%2F", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["AccountId"] = accountId.ToString(),
                ["Date"] = "2026-01-05",
                ["Balance"] = "100.00",
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Accounts/Details", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task OversizedNotes_PostedThroughTheForm_AreStoredShortened()
    {
        var (client, userId, accountId) = await SignedInUserAsync();
        int id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SetCurrentUser(userId);
            var txn = new Transaction { AccountId = accountId, Date = new DateOnly(2026, 1, 5), Amount = -1, Description = "Coffee" };
            db.Transactions.Add(txn);
            await db.SaveChangesAsync();
            id = txn.Id;
        }
        var token = HttpTestHelpers.ExtractAntiforgeryToken(await client.GetStringAsync("/Transactions"));

        var response = await client.PostAsync("/Transactions/UpdateNotes", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = id.ToString(),
                ["notes"] = new string('n', 500_000),
                ["__RequestVerificationToken"] = token
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var check = _factory.Services.CreateScope();
        var read = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        read.SetCurrentUser(userId);
        Assert.Equal(ApplicationDbContext.MaxStoredTextLength, (await read.Transactions.SingleAsync(t => t.Id == id)).Notes!.Length);
    }
}
