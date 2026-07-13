using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;
using MatchType = Plannit.Models.Entities.MatchType;

namespace Plannit.Services;

public class DataManagementService
{
    private readonly ApplicationDbContext _db;

    public DataManagementService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<ImportBatch>> GetImportBatchesAsync()
    {
        return await _db.ImportBatches
            .AsNoTracking()
            .Include(b => b.Account)
            .OrderByDescending(b => b.ImportedAt)
            .ToListAsync();
    }

    public async Task<ImportBatch?> GetImportBatchAsync(int id)
    {
        return await _db.ImportBatches
            .AsNoTracking()
            .Include(b => b.Account)
            .Include(b => b.Transactions)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<(bool Success, string Message)> UndoImportBatchAsync(int batchId)
    {
        var batch = await _db.ImportBatches
            .Include(b => b.Transactions)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch is null)
            return (false, "Import batch not found.");

        var editedCount = batch.Transactions.Count(t =>
            t.CategoryId is not null || t.Notes is not null || t.SplitGroupId is not null);

        _db.Transactions.RemoveRange(batch.Transactions);
        _db.ImportBatches.Remove(batch);
        await _db.SaveChangesAsync();

        var msg = $"Deleted {batch.Transactions.Count} transactions from batch \"{batch.FileName}\".";
        if (editedCount > 0)
            msg += $" ({editedCount} had been modified since import.)";

        return (true, msg);
    }

    public async Task<int> GetBatchEditedCountAsync(int batchId)
    {
        return await _db.Transactions
            .Where(t => t.ImportBatchId == batchId)
            .CountAsync(t => t.CategoryId != null || t.Notes != null || t.SplitGroupId != null);
    }

    public async Task<string> ExportFullBackupJsonAsync()
    {
        var accounts = await _db.Accounts
            .AsNoTracking()
            .Include(a => a.Snapshots)
            .ToListAsync();

        var transactions = await _db.Transactions.AsNoTracking().ToListAsync();
        var categories = await _db.Categories.AsNoTracking().ToListAsync();
        var rules = await _db.CategoryRules.AsNoTracking().ToListAsync();
        var budgets = await _db.Budgets.AsNoTracking().Include(b => b.Category).ToListAsync();
        var scenarios = await _db.ProjectionScenarios
            .AsNoTracking()
            .Include(s => s.AccountAssumptions)
            .Include(s => s.Events)
            .ToListAsync();

        var export = new FullExportModel
        {
            ExportedAt = DateTime.UtcNow,
            Accounts = accounts.Select(a => new ExportAccount
            {
                Name = a.Name,
                Type = a.Type.ToString(),
                Institution = a.Institution,
                IsActive = a.IsActive,
                Snapshots = a.Snapshots.Select(s => new ExportSnapshot
                {
                    Date = s.Date,
                    Balance = s.Balance
                }).OrderBy(s => s.Date).ToList()
            }).ToList(),
            Transactions = transactions.Select(t => new ExportTransaction
            {
                AccountId = t.AccountId,
                Date = t.Date,
                Amount = t.Amount,
                Description = t.Description,
                OriginalDescription = t.OriginalDescription,
                CategoryName = categories.FirstOrDefault(c => c.Id == t.CategoryId)?.Name,
                Notes = t.Notes,
                SplitGroupId = t.SplitGroupId
            }).OrderBy(t => t.Date).ToList(),
            Categories = categories.Select(c => new ExportCategory
            {
                Name = c.Name,
                ParentName = categories.FirstOrDefault(p => p.Id == c.ParentId)?.Name,
                IsSystem = c.IsSystem
            }).ToList(),
            Rules = rules.Select(r => new ExportRule
            {
                MatchText = r.MatchText,
                MatchType = r.MatchType.ToString(),
                CategoryName = categories.FirstOrDefault(c => c.Id == r.CategoryId)?.Name ?? "",
                Priority = r.Priority
            }).ToList(),
            Budgets = budgets.Select(b => new ExportBudget
            {
                CategoryName = b.Category?.Name ?? "",
                MonthlyAmount = b.MonthlyAmount,
                StartMonth = b.StartMonth,
                EndMonth = b.EndMonth
            }).ToList(),
            Scenarios = scenarios.Select(s => new ExportScenario
            {
                Name = s.Name,
                BirthYear = s.BirthYear,
                RetirementAge = s.RetirementAge,
                LifeExpectancy = s.LifeExpectancy,
                AnnualRetirementSpending = s.AnnualRetirementSpending,
                InflationRate = s.InflationRate,
                ReturnStdDev = s.ReturnStdDev,
                AccountAssumptions = s.AccountAssumptions.Select(a => new ExportAccountAssumption
                {
                    AccountName = accounts.FirstOrDefault(acc => acc.Id == a.AccountId)?.Name ?? "",
                    AnnualContribution = a.AnnualContribution,
                    EmployerMatch = a.EmployerMatch,
                    ExpectedReturnRate = a.ExpectedReturnRate,
                    ContributionEndAge = a.ContributionEndAge
                }).ToList(),
                Events = s.Events.Select(e => new ExportEvent
                {
                    Name = e.Name,
                    Age = e.Age,
                    Amount = e.Amount,
                    IsRecurring = e.IsRecurring,
                    EndAge = e.EndAge
                }).ToList()
            }).ToList()
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    public async Task<int> InvertAccountTransactionSignsAsync(int accountId)
    {
        var transactions = await _db.Transactions.Where(t => t.AccountId == accountId).ToListAsync();

        foreach (var txn in transactions)
        {
            txn.Amount = -txn.Amount;
            if (txn.ImportHash is not null)
                txn.ImportHash = CsvImportService.ComputeImportHash(txn.AccountId, txn.Date, txn.Amount, txn.OriginalDescription ?? txn.Description);
        }

        if (transactions.Count > 0)
            await _db.SaveChangesAsync();

        return transactions.Count;
    }

    public async Task<int> BulkSetCategoryAsync(List<int> transactionIds, int categoryId)
    {
        // Query filters only scope reads; a posted foreign CategoryId must be rejected here.
        if (!await _db.Categories.AnyAsync(c => c.Id == categoryId)) return 0;

        var count = await _db.Transactions
            .Where(t => transactionIds.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, categoryId));
        return count;
    }

    public async Task<int> BulkDeleteAsync(List<int> transactionIds)
    {
        var count = await _db.Transactions
            .Where(t => transactionIds.Contains(t.Id))
            .ExecuteDeleteAsync();
        if (count > 0)
            _db.BumpCacheVersion();
        return count;
    }

    public async Task<(bool Success, string Message)> MergeCategoriesAsync(int sourceId, int targetId)
    {
        if (sourceId == targetId)
            return (false, "Cannot merge a category into itself.");

        var source = await _db.Categories.FirstOrDefaultAsync(c => c.Id == sourceId);
        var target = await _db.Categories.FirstOrDefaultAsync(c => c.Id == targetId);

        if (source is null || target is null)
            return (false, "One or both categories not found.");

        await _db.Transactions
            .Where(t => t.CategoryId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, targetId));

        await _db.CategoryRules
            .Where(r => r.CategoryId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CategoryId, targetId));

        await _db.Budgets
            .Where(b => b.CategoryId == sourceId)
            .ExecuteDeleteAsync();

        var children = await _db.Categories.Where(c => c.ParentId == sourceId).ToListAsync();
        foreach (var child in children)
            child.ParentId = source.ParentId;

        _db.Categories.Remove(source);
        await _db.SaveChangesAsync();

        return (true, $"Merged \"{source.Name}\" into \"{target.Name}\".");
    }

    public async Task<List<Transaction>> TestRuleMatchesAsync(string matchText, MatchType matchType)
    {
        var allTransactions = await _db.Transactions
            .AsNoTracking()
            .Include(t => t.Account)
            .Include(t => t.Category)
            .ToListAsync();

        var rule = new CategoryRule { MatchText = matchText, MatchType = matchType };
        return allTransactions
            .Where(t => Matches(t.Description, rule) ||
                        (t.OriginalDescription is not null && Matches(t.OriginalDescription, rule)))
            .OrderByDescending(t => t.Date)
            .Take(100)
            .ToList();
    }

    public async Task<bool> MoveRulePriorityAsync(int ruleId, bool moveUp)
    {
        var rules = await _db.CategoryRules
            .OrderByDescending(r => r.Priority)
            .ToListAsync();

        var rule = rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return false;

        var index = rules.IndexOf(rule);
        var swapIndex = moveUp ? index - 1 : index + 1;

        if (swapIndex < 0 || swapIndex >= rules.Count) return false;

        var other = rules[swapIndex];
        (rule.Priority, other.Priority) = (other.Priority, rule.Priority);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateTransactionNotesAsync(int id, string? notes)
    {
        var txn = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == id);
        if (txn is null) return false;

        txn.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<Transaction>> SplitTransactionAsync(int transactionId, List<(decimal Amount, string Description, int? CategoryId)> splits)
    {
        var original = await _db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId);
        if (original is null) return new();

        var splitCategoryIds = splits
            .Where(s => s.CategoryId is not null)
            .Select(s => s.CategoryId!.Value)
            .Distinct()
            .ToList();
        if (splitCategoryIds.Count > 0)
        {
            var ownedCount = await _db.Categories.CountAsync(c => splitCategoryIds.Contains(c.Id));
            if (ownedCount != splitCategoryIds.Count) return new();
        }

        var total = splits.Sum(s => s.Amount);
        if (Math.Abs(total - original.Amount) > 0.01m)
            return new();

        var groupId = Guid.NewGuid();
        var newTransactions = new List<Transaction>();

        foreach (var (amount, description, categoryId) in splits)
        {
            newTransactions.Add(new Transaction
            {
                AccountId = original.AccountId,
                Date = original.Date,
                Amount = amount,
                Description = description,
                OriginalDescription = original.Description,
                CategoryId = categoryId,
                ImportBatchId = original.ImportBatchId,
                SplitGroupId = groupId,
                Notes = original.Notes
            });
        }

        _db.Transactions.Remove(original);
        _db.Transactions.AddRange(newTransactions);
        await _db.SaveChangesAsync();

        return newTransactions;
    }

    private static bool Matches(string text, CategoryRule rule)
    {
        return rule.MatchType switch
        {
            MatchType.Contains => text.Contains(rule.MatchText, StringComparison.OrdinalIgnoreCase),
            MatchType.StartsWith => text.StartsWith(rule.MatchText, StringComparison.OrdinalIgnoreCase),
            MatchType.Regex => CategorizationService.SafeRegexMatch(text, rule.MatchText),
            _ => false
        };
    }
}

public class FullExportModel
{
    public DateTime ExportedAt { get; set; }
    public List<ExportAccount> Accounts { get; set; } = new();
    public List<ExportTransaction> Transactions { get; set; } = new();
    public List<ExportCategory> Categories { get; set; } = new();
    public List<ExportRule> Rules { get; set; } = new();
    public List<ExportBudget> Budgets { get; set; } = new();
    public List<ExportScenario> Scenarios { get; set; } = new();
}

public class ExportAccount
{
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string? Institution { get; set; }
    public bool IsActive { get; set; }
    public List<ExportSnapshot> Snapshots { get; set; } = new();
}

public class ExportSnapshot
{
    public DateOnly Date { get; set; }
    public decimal Balance { get; set; }
}

public class ExportTransaction
{
    public int AccountId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = null!;
    public string? OriginalDescription { get; set; }
    public string? CategoryName { get; set; }
    public string? Notes { get; set; }
    public Guid? SplitGroupId { get; set; }
}

public class ExportCategory
{
    public string Name { get; set; } = null!;
    public string? ParentName { get; set; }
    public bool IsSystem { get; set; }
}

public class ExportRule
{
    public string MatchText { get; set; } = null!;
    public string MatchType { get; set; } = null!;
    public string CategoryName { get; set; } = null!;
    public int Priority { get; set; }
}

public class ExportBudget
{
    public string CategoryName { get; set; } = null!;
    public decimal MonthlyAmount { get; set; }
    public DateOnly StartMonth { get; set; }
    public DateOnly? EndMonth { get; set; }
}

public class ExportScenario
{
    public string Name { get; set; } = null!;
    public int BirthYear { get; set; }
    public int RetirementAge { get; set; }
    public int LifeExpectancy { get; set; }
    public decimal AnnualRetirementSpending { get; set; }
    public decimal InflationRate { get; set; }
    public decimal ReturnStdDev { get; set; }
    public List<ExportAccountAssumption> AccountAssumptions { get; set; } = new();
    public List<ExportEvent> Events { get; set; } = new();
}

public class ExportAccountAssumption
{
    public string AccountName { get; set; } = null!;
    public decimal AnnualContribution { get; set; }
    public decimal EmployerMatch { get; set; }
    public decimal ExpectedReturnRate { get; set; }
    public int ContributionEndAge { get; set; }
}

public class ExportEvent
{
    public string Name { get; set; } = null!;
    public int Age { get; set; }
    public decimal Amount { get; set; }
    public bool IsRecurring { get; set; }
    public int? EndAge { get; set; }
}
