using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class AccountService
{
    private readonly ApplicationDbContext _db;

    public AccountService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<Account>> GetAllAsync()
    {
        return await _db.Accounts
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<Account?> GetByIdAsync(int id)
    {
        return await _db.Accounts
            .Include(a => a.Snapshots.OrderByDescending(s => s.Date))
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Account> CreateAsync(string userId, string name, AccountType type, string? institution)
    {
        var account = new Account
        {
            UserId = userId,
            Name = name,
            Type = type,
            Institution = institution
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    public async Task<bool> UpdateAsync(int id, string name, AccountType type, string? institution)
    {
        var account = await _db.Accounts.FindAsync(id);
        if (account is null) return false;

        account.Name = name;
        account.Type = type;
        account.Institution = institution;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeactivateAsync(int id)
    {
        var account = await _db.Accounts.FindAsync(id);
        if (account is null) return false;

        account.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<BalanceSnapshot> AddSnapshotAsync(int accountId, DateOnly date, decimal balance)
    {
        var existing = await _db.BalanceSnapshots
            .FirstOrDefaultAsync(s => s.AccountId == accountId && s.Date == date);

        if (existing is not null)
        {
            existing.Balance = balance;
            await _db.SaveChangesAsync();
            return existing;
        }

        var snapshot = new BalanceSnapshot
        {
            AccountId = accountId,
            Date = date,
            Balance = balance
        };
        _db.BalanceSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
        return snapshot;
    }

    public async Task<bool> DeleteSnapshotAsync(int snapshotId)
    {
        var snapshot = await _db.BalanceSnapshots.FindAsync(snapshotId);
        if (snapshot is null) return false;

        _db.BalanceSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync();
        return true;
    }
}
