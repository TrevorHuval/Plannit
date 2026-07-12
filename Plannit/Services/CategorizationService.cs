using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using MatchType = Plannit.Models.Entities.MatchType;

namespace Plannit.Services;

public class CategorizationService
{
    private readonly ApplicationDbContext _db;

    public CategorizationService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Category>> GetAllCategoriesAsync()
    {
        return await _db.Categories
            .Include(c => c.Children)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<Category?> GetCategoryByIdAsync(int id)
    {
        return await _db.Categories
            .Include(c => c.Rules.OrderByDescending(r => r.Priority))
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Category> CreateCategoryAsync(string userId, string name, int? parentId, bool isSystem = false)
    {
        var category = new Category
        {
            UserId = userId,
            Name = name,
            ParentId = parentId,
            IsSystem = isSystem
        };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }

    public async Task<bool> UpdateCategoryAsync(int id, string name, int? parentId)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (category is null) return false;

        category.Name = name;
        category.ParentId = parentId;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteCategoryAsync(int id)
    {
        var category = await _db.Categories
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (category is null) return false;

        foreach (var child in category.Children)
            child.ParentId = category.ParentId;

        await _db.Transactions
            .Where(t => t.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, (int?)null));

        await _db.CategoryRules
            .Where(r => r.CategoryId == id)
            .ExecuteDeleteAsync();

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<CategoryRule>> GetRulesAsync()
    {
        return await _db.CategoryRules
            .Include(r => r.Category)
            .OrderByDescending(r => r.Priority)
            .ToListAsync();
    }

    public async Task<CategoryRule> CreateRuleAsync(string userId, string matchText, MatchType matchType, int categoryId, int priority)
    {
        var rule = new CategoryRule
        {
            UserId = userId,
            MatchText = matchText,
            MatchType = matchType,
            CategoryId = categoryId,
            Priority = priority
        };
        _db.CategoryRules.Add(rule);
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task<bool> UpdateRuleAsync(int id, string matchText, MatchType matchType, int categoryId, int priority)
    {
        var rule = await _db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return false;

        rule.MatchText = matchText;
        rule.MatchType = matchType;
        rule.CategoryId = categoryId;
        rule.Priority = priority;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteRuleAsync(int id)
    {
        var rule = await _db.CategoryRules.FirstOrDefaultAsync(r => r.Id == id);
        if (rule is null) return false;

        _db.CategoryRules.Remove(rule);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> CategorizeTransactionAsync(int transactionId, int? categoryId)
    {
        var txn = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
        if (txn is null) return 0;

        txn.CategoryId = categoryId;
        await _db.SaveChangesAsync();
        return 1;
    }

    public async Task<int> ApplyRulesToUncategorizedAsync()
    {
        var rules = await _db.CategoryRules
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        if (rules.Count == 0) return 0;

        var uncategorized = await _db.Transactions
            .Where(t => t.CategoryId == null)
            .ToListAsync();

        int count = 0;
        foreach (var txn in uncategorized)
        {
            var matchedRule = FindMatchingRule(txn.Description, txn.OriginalDescription, rules);
            if (matchedRule is not null)
            {
                txn.CategoryId = matchedRule.CategoryId;
                count++;
            }
        }

        if (count > 0)
            await _db.SaveChangesAsync();

        return count;
    }

    public async Task CategorizeImportedTransactionsAsync(List<Transaction> transactions)
    {
        var rules = await _db.CategoryRules
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        if (rules.Count == 0) return;

        foreach (var txn in transactions)
        {
            if (txn.CategoryId is not null) continue;

            var matchedRule = FindMatchingRule(txn.Description, txn.OriginalDescription, rules);
            if (matchedRule is not null)
                txn.CategoryId = matchedRule.CategoryId;
        }
    }

    public async Task EnsureDefaultCategoriesAsync(string userId)
    {
        if (await _db.Categories.AnyAsync())
            return;

        var defaults = new (string Name, string[] RuleTexts)[]
        {
            ("Groceries", ["Whole Foods", "Kroger", "Trader Joe", "Aldi", "Safeway", "Publix", "HEB", "Costco", "Walmart Grocery"]),
            ("Dining", ["Starbucks", "Uber Eats", "DoorDash", "Grubhub", "McDonald", "Chipotle", "Restaurant"]),
            ("Utilities", ["Electric", "Water Utility", "Gas Company", "Utility", "Sewer"]),
            ("Housing", ["Rent", "Mortgage", "HOA"]),
            ("Transport", ["Shell", "Chevron", "Exxon", "Gas Station", "Uber", "Lyft", "Parking"]),
            ("Shopping", ["Amazon", "Target", "Walmart", "Best Buy", "Costco"]),
            ("Entertainment", ["Netflix", "Spotify", "Hulu", "Disney+", "HBO", "Apple TV", "YouTube"]),
            ("Phone & Internet", ["AT&T", "Verizon", "T-Mobile", "Comcast", "Spectrum"]),
            ("Insurance", ["Insurance", "Geico", "State Farm", "Allstate"]),
            ("Healthcare", ["Pharmacy", "CVS", "Walgreens", "Doctor", "Hospital", "Dentist"]),
            ("Income", ["Payroll", "Direct Deposit", "Salary"]),
            ("Transfers", ["Transfer", "Zelle", "Venmo", "Payment From", "Payment To"]),
            ("Interest & Fees", ["Interest", "Fee", "Dividend"]),
        };

        foreach (var (name, ruleTexts) in defaults)
        {
            var category = new Category
            {
                UserId = userId,
                Name = name,
                IsSystem = true
            };
            _db.Categories.Add(category);
            await _db.SaveChangesAsync();

            int priority = 100;
            foreach (var text in ruleTexts)
            {
                _db.CategoryRules.Add(new CategoryRule
                {
                    UserId = userId,
                    MatchText = text,
                    MatchType = MatchType.Contains,
                    CategoryId = category.Id,
                    Priority = priority--
                });
            }
            await _db.SaveChangesAsync();
        }
    }

    private static CategoryRule? FindMatchingRule(string description, string? originalDescription, List<CategoryRule> rules)
    {
        foreach (var rule in rules)
        {
            if (Matches(description, rule) || (originalDescription is not null && Matches(originalDescription, rule)))
                return rule;
        }
        return null;
    }

    private static bool Matches(string text, CategoryRule rule)
    {
        return rule.MatchType switch
        {
            MatchType.Contains => text.Contains(rule.MatchText, StringComparison.OrdinalIgnoreCase),
            MatchType.StartsWith => text.StartsWith(rule.MatchText, StringComparison.OrdinalIgnoreCase),
            MatchType.Regex => Regex.IsMatch(text, rule.MatchText, RegexOptions.IgnoreCase),
            _ => false
        };
    }
}
