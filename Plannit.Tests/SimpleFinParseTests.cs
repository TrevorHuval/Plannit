using System.Text;
using Plannit.Services.Sync;

namespace Plannit.Tests;

public class SimpleFinParseTests
{
    // A trimmed but structurally faithful SimpleFIN /accounts payload:
    // a checking account (positive balance) and a credit card (negative balance),
    // amounts as strings, timestamps as unix seconds.
    private const string SampleJson = """
        {
          "errors": [],
          "accounts": [
            {
              "org": { "domain": "mybank.com", "name": "My Bank" },
              "id": "ACT-CHK-1",
              "name": "Everyday Checking",
              "currency": "USD",
              "balance": "1523.44",
              "available-balance": "1400.00",
              "balance-date": 1752278400,
              "transactions": [
                { "id": "T1", "posted": 1752192000, "amount": "-42.10", "description": "UBER EATS", "payee": "Uber Eats" },
                { "id": "T2", "posted": 1752105600, "amount": "3200.00", "description": "ACME PAYROLL", "payee": "" }
              ]
            },
            {
              "org": { "name": "Card Co" },
              "id": "ACT-CC-9",
              "name": "Rewards Visa",
              "currency": "USD",
              "balance": "-110.46",
              "balance-date": 1752278400,
              "transactions": [
                { "id": "T9", "posted": 1752192000, "amount": "-9.99", "memo": "NETFLIX.COM" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ParseAccountsJson_ParsesAccountsBalancesAndTransactions()
    {
        var set = SimpleFinClient.ParseAccountsJson(SampleJson);

        Assert.Empty(set.Errors);
        Assert.Equal(2, set.Accounts.Count);

        var chk = set.Accounts.Single(a => a.Id == "ACT-CHK-1");
        Assert.Equal("Everyday Checking", chk.Name);
        Assert.Equal("My Bank", chk.OrgName);
        Assert.Equal(1523.44m, chk.Balance);
        Assert.Equal(new DateOnly(2025, 7, 12), chk.BalanceDate);
        Assert.Equal(2, chk.Transactions.Count);

        var card = set.Accounts.Single(a => a.Id == "ACT-CC-9");
        Assert.Equal(-110.46m, card.Balance);
    }

    [Fact]
    public void ParseAccountsJson_ConvertsUnixTimestampsAndSignedAmounts()
    {
        var set = SimpleFinClient.ParseAccountsJson(SampleJson);
        var t1 = set.Accounts.Single(a => a.Id == "ACT-CHK-1").Transactions.Single(t => t.Id == "T1");

        Assert.Equal(new DateOnly(2025, 7, 11), t1.Posted);
        Assert.Equal(-42.10m, t1.Amount); // debit stays negative — matches Plannit's convention
    }

    [Fact]
    public void SimpleFinTransaction_BestDescription_PrefersPayeeThenDescriptionThenMemo()
    {
        var set = SimpleFinClient.ParseAccountsJson(SampleJson);
        var chk = set.Accounts.Single(a => a.Id == "ACT-CHK-1");

        // Payee present → used.
        Assert.Equal("Uber Eats", chk.Transactions.Single(t => t.Id == "T1").BestDescription);
        // Payee blank → falls back to description.
        Assert.Equal("ACME PAYROLL", chk.Transactions.Single(t => t.Id == "T2").BestDescription);
        // Only memo present → memo.
        var card = set.Accounts.Single(a => a.Id == "ACT-CC-9");
        Assert.Equal("NETFLIX.COM", card.Transactions.Single(t => t.Id == "T9").BestDescription);
    }

    [Fact]
    public void ParseAccountsJson_CollectsProviderErrors()
    {
        var json = """{ "errors": ["Connection to My Bank may need attention"], "accounts": [] }""";
        var set = SimpleFinClient.ParseAccountsJson(json);

        Assert.Single(set.Errors);
        Assert.Contains("attention", set.Errors[0]);
        Assert.Empty(set.Accounts);
    }

    [Fact]
    public void ParseAccountsJson_ToleratesMissingOptionalFields()
    {
        var json = """{ "accounts": [ { "id": "X", "transactions": [] } ] }""";
        var set = SimpleFinClient.ParseAccountsJson(json);

        var acct = Assert.Single(set.Accounts);
        Assert.Equal("X", acct.Id);
        Assert.Null(acct.Balance);
        Assert.Null(acct.BalanceDate);
        Assert.Empty(acct.Transactions);
    }

    [Fact]
    public void DecodeSetupToken_DecodesBase64ClaimUrl()
    {
        var claimUrl = "https://bridge.simplefin.org/simplefin/claim/DEMO123";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl));

        Assert.Equal(claimUrl, SimpleFinClient.DecodeSetupToken(token));
    }

    [Fact]
    public void DecodeSetupToken_RejectsNonBase64()
    {
        Assert.Throws<ArgumentException>(() => SimpleFinClient.DecodeSetupToken("not base64!!!"));
    }

    [Fact]
    public void DecodeSetupToken_RejectsBase64ThatIsNotAUrl()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("just some text"));
        Assert.Throws<ArgumentException>(() => SimpleFinClient.DecodeSetupToken(token));
    }

    [Fact]
    public void BuildAccountsRequest_StripsCredentialsAndAddsBasicAuth()
    {
        var accessUrl = "https://user123:pass456@beta-bridge.simplefin.org/simplefin";

        var (uri, auth) = SimpleFinClient.BuildAccountsRequest(accessUrl, null);

        Assert.Equal("https://beta-bridge.simplefin.org/simplefin/accounts", uri.GetLeftPart(UriPartial.Path));
        Assert.Empty(uri.UserInfo); // credentials never travel in the URL
        Assert.Equal("Basic", auth.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("user123:pass456", decoded);
    }

    [Fact]
    public void BuildAccountsRequest_AddsStartDateAsUnixSeconds()
    {
        var accessUrl = "https://u:p@bridge.simplefin.org/simplefin";
        var start = new DateOnly(2026, 1, 1);

        var (uri, _) = SimpleFinClient.BuildAccountsRequest(accessUrl, start);

        var expectedEpoch = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();
        Assert.Contains($"start-date={expectedEpoch}", uri.Query);
    }
}
