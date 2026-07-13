using Plannit.Services.Ai;

namespace Plannit.Tests;

public class SmartCategorizationPromptTests
{
    private static SmartCategorizationRequest Request(int categoryCount = 3, int maxCategories = 25)
    {
        var groups = new[]
        {
            new MerchantGroup("NETFLIX", new[] { "NETFLIX.COM" }, 4, -15.99m, -1),
            new MerchantGroup("SHELL", new[] { "SHELL OIL 12345" }, 2, -42.10m, -1),
        };
        var categories = Enumerable.Range(1, categoryCount).Select(i => $"Category{i}").ToArray();
        return new SmartCategorizationRequest(groups, categories, maxCategories);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var req = Request();
        var a = SmartCategorizationPrompt.Build(req);
        var b = SmartCategorizationPrompt.Build(req);

        Assert.Equal(a.System, b.System);
        Assert.Equal(a.User, b.User);
    }

    [Fact]
    public void System_DocumentsTheJsonSchema()
    {
        var (system, _) = SmartCategorizationPrompt.Build(Request());

        Assert.Contains("\"suggestions\"", system);
        Assert.Contains("\"merchantKey\"", system);
        Assert.Contains("\"category\"", system);
        Assert.Contains("\"confidence\"", system);
        Assert.Contains("\"isNew\"", system);
        // Conservative behavior must be stated.
        Assert.Contains("null", system);
    }

    [Fact]
    public void User_ListsCategoriesAndMerchantData()
    {
        var (_, user) = SmartCategorizationPrompt.Build(Request());

        Assert.Contains("Existing categories:", user);
        Assert.Contains("Category1", user);
        Assert.Contains("NETFLIX", user);
        Assert.Contains("SHELL", user);
        // Sign is expressed in words, not raw negative numbers with the merchant.
        Assert.Contains("\"sign\":\"out\"", user);
    }

    [Fact]
    public void System_UnderCap_AllowsBoundedNewCategories()
    {
        var (system, _) = SmartCategorizationPrompt.Build(Request(categoryCount: 20, maxCategories: 25));

        Assert.Contains("NEW category", system);
        Assert.Contains("at most 5", system); // 25 - 20 remaining slots
    }

    [Fact]
    public void System_AtCap_ForbidsNewCategories()
    {
        var (system, _) = SmartCategorizationPrompt.Build(Request(categoryCount: 25, maxCategories: 25));

        Assert.Contains("AT the category cap", system);
        Assert.Contains("Do NOT suggest any new categories", system);
    }
}
