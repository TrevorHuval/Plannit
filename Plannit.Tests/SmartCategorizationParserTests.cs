using Plannit.Services.Ai;

namespace Plannit.Tests;

public class SmartCategorizationParserTests
{
    private static SmartCategorizationRequest Request(
        string[]? merchants = null, string[]? categories = null, int categoryCount = -1, int maxCategories = 25)
    {
        merchants ??= new[] { "NETFLIX", "SHELL", "MYSTERY LLC" };
        categories ??= new[] { "Entertainment", "Transport" };
        var groups = merchants.Select(m => new MerchantGroup(m, new[] { m }, 3, -10m, -1)).ToList();
        // categoryCount lets tests simulate a larger existing set than the names we pass through.
        var names = categoryCount < 0 ? categories : Enumerable.Range(1, categoryCount).Select(i => $"Cat{i}").ToArray();
        return new SmartCategorizationRequest(groups, names, maxCategories);
    }

    [Fact]
    public void Parse_ValidResponse_MapsExistingAndNew()
    {
        var json = """
        {"suggestions":[
          {"merchantKey":"NETFLIX","category":"Entertainment","confidence":0.95,"isNew":false},
          {"merchantKey":"MYSTERY LLC","category":"Pet Care","confidence":0.8,"isNew":true}
        ]}
        """;

        var result = SmartCategorizationResponseParser.Parse(json, Request());

        var netflix = Assert.Single(result, p => p.MerchantKey == "NETFLIX");
        Assert.Equal("Entertainment", netflix.CategoryName);
        Assert.False(netflix.IsNewCategorySuggestion);

        var mystery = Assert.Single(result, p => p.MerchantKey == "MYSTERY LLC");
        Assert.Equal("Pet Care", mystery.CategoryName);
        Assert.True(mystery.IsNewCategorySuggestion);
    }

    [Fact]
    public void Parse_ResolvesExistingCategoryEvenIfModelSaysNew()
    {
        // Model claims a new category but the name already exists — trust our list, not the flag.
        var json = """{"suggestions":[{"merchantKey":"NETFLIX","category":"entertainment","confidence":0.9,"isNew":true}]}""";

        var result = SmartCategorizationResponseParser.Parse(json, Request());

        var p = Assert.Single(result);
        Assert.Equal("Entertainment", p.CategoryName); // canonical casing from existing list
        Assert.False(p.IsNewCategorySuggestion);
    }

    [Fact]
    public void Parse_IgnoresUnknownMerchantKeys()
    {
        var json = """{"suggestions":[{"merchantKey":"NOT_IN_REQUEST","category":"Entertainment","confidence":0.9,"isNew":false}]}""";

        var result = SmartCategorizationResponseParser.Parse(json, Request());

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ClampsConfidenceAndHandlesNullCategory()
    {
        var json = """
        {"suggestions":[
          {"merchantKey":"NETFLIX","category":"Entertainment","confidence":1.7,"isNew":false},
          {"merchantKey":"SHELL","category":null,"confidence":0.1,"isNew":false}
        ]}
        """;

        var result = SmartCategorizationResponseParser.Parse(json, Request());

        Assert.Equal(1.0, Assert.Single(result, p => p.MerchantKey == "NETFLIX").Confidence);
        var shell = Assert.Single(result, p => p.MerchantKey == "SHELL");
        Assert.Null(shell.CategoryName);
    }

    [Fact]
    public void Parse_MarkdownFencedJson_StillParses()
    {
        var raw = "Here you go:\n```json\n{\"suggestions\":[{\"merchantKey\":\"NETFLIX\",\"category\":\"Entertainment\",\"confidence\":0.9,\"isNew\":false}]}\n```";

        var result = SmartCategorizationResponseParser.Parse(raw, Request());

        Assert.Single(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ this is : broken")]
    [InlineData("{\"unexpected\":\"shape\"}")]
    public void Parse_MalformedOrPartial_DegradesToEmpty(string raw)
    {
        var result = SmartCategorizationResponseParser.Parse(raw, Request());
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_AtCategoryCap_DropsNewSuggestions()
    {
        // 25 existing categories, cap 25 → no new categories may be created.
        var req = Request(merchants: new[] { "MYSTERY LLC" }, categoryCount: 25, maxCategories: 25);
        var json = """{"suggestions":[{"merchantKey":"MYSTERY LLC","category":"Brand New Thing","confidence":0.9,"isNew":true}]}""";

        var result = SmartCategorizationResponseParser.Parse(json, req);

        var p = Assert.Single(result);
        Assert.Null(p.CategoryName); // downgraded to "no suggestion"
        Assert.False(p.IsNewCategorySuggestion);
    }

    [Fact]
    public void Parse_LimitsNewCategoriesToRemainingSlots()
    {
        // 24 existing, cap 25 → exactly one new category allowed; keep the higher-confidence one.
        var req = Request(merchants: new[] { "AAA", "BBB" }, categoryCount: 24, maxCategories: 25);
        var json = """
        {"suggestions":[
          {"merchantKey":"AAA","category":"New Alpha","confidence":0.6,"isNew":true},
          {"merchantKey":"BBB","category":"New Beta","confidence":0.95,"isNew":true}
        ]}
        """;

        var result = SmartCategorizationResponseParser.Parse(json, req);

        var beta = Assert.Single(result, p => p.MerchantKey == "BBB");
        Assert.Equal("New Beta", beta.CategoryName);
        Assert.True(beta.IsNewCategorySuggestion);

        var alpha = Assert.Single(result, p => p.MerchantKey == "AAA");
        Assert.Null(alpha.CategoryName); // over the single remaining slot → dropped
    }
}
