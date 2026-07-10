using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class TransactionService
{
    private readonly ApplicationDbContext _db;

    public TransactionService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<(List<Transaction> Items, int TotalCount)> GetFilteredAsync(
        int? accountId, DateOnly? startDate, DateOnly? endDate, string? searchText, int? categoryId, int page, int pageSize = 50)
    {
        var query = _db.Transactions
            .Include(t => t.Account)
            .Include(t => t.Category)
            .AsQueryable();

        if (accountId.HasValue)
            query = query.Where(t => t.AccountId == accountId.Value);

        if (startDate.HasValue)
            query = query.Where(t => t.Date >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(t => t.Date <= endDate.Value);

        if (!string.IsNullOrWhiteSpace(searchText))
            query = query.Where(t => t.Description.Contains(searchText) ||
                                     (t.OriginalDescription != null && t.OriginalDescription.Contains(searchText)));

        if (categoryId.HasValue)
        {
            if (categoryId.Value == -1)
                query = query.Where(t => t.CategoryId == null);
            else
                query = query.Where(t => t.CategoryId == categoryId.Value);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<Transaction?> GetByIdAsync(int id)
    {
        return await _db.Transactions
            .Include(t => t.Account)
            .Include(t => t.Category)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Transaction> CreateAsync(int accountId, DateOnly date, decimal amount, string description)
    {
        var transaction = new Transaction
        {
            AccountId = accountId,
            Date = date,
            Amount = amount,
            Description = description
        };
        _db.Transactions.Add(transaction);
        await _db.SaveChangesAsync();
        return transaction;
    }

    public async Task<bool> UpdateAsync(int id, int accountId, DateOnly date, decimal amount, string description)
    {
        var transaction = await _db.Transactions.FindAsync(id);
        if (transaction is null) return false;

        transaction.AccountId = accountId;
        transaction.Date = date;
        transaction.Amount = amount;
        transaction.Description = description;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var transaction = await _db.Transactions.FindAsync(id);
        if (transaction is null) return false;

        _db.Transactions.Remove(transaction);
        await _db.SaveChangesAsync();
        return true;
    }
}
