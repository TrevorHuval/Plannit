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
            .AsNoTracking()
            .Where(a => a.IsActive)
            .Include(a => a.Snapshots)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<Account?> GetByIdAsync(int id)
    {
        return await _db.Accounts
            .AsNoTracking()
            .Include(a => a.Snapshots.OrderByDescending(s => s.Date))
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<Account> CreateAsync(string userId, string name, AccountType type, string? institution,
        decimal? interestRate = null, decimal? minimumPayment = null, decimal? originalPrincipal = null)
    {
        var account = new Account
        {
            UserId = userId,
            Name = name,
            Type = type,
            Institution = institution,
            InterestRate = interestRate,
            MinimumPayment = minimumPayment,
            OriginalPrincipal = originalPrincipal
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    /// <summary>
    /// Updates an account. When <paramref name="rowVersion"/> is supplied (from an edit form),
    /// it is enforced as the optimistic-concurrency token — a stale value throws
    /// <see cref="DbUpdateConcurrencyException"/> for the caller to surface a friendly conflict message.
    /// </summary>
    public async Task<bool> UpdateAsync(int id, string name, AccountType type, string? institution, Guid? rowVersion = null,
        decimal? interestRate = null, decimal? minimumPayment = null, decimal? originalPrincipal = null)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return false;

        if (rowVersion.HasValue)
            _db.Entry(account).Property(a => a.RowVersion).OriginalValue = rowVersion.Value;

        account.Name = name;
        account.Type = type;
        account.Institution = institution;
        account.InterestRate = interestRate;
        account.MinimumPayment = minimumPayment;
        account.OriginalPrincipal = originalPrincipal;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeactivateAsync(int id)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == id);
        if (account is null) return false;

        account.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<BalanceSnapshot?> AddSnapshotAsync(int accountId, DateOnly date, decimal balance)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account is null) return null;

        var normalizedBalance = AccountConventions.NormalizeSnapshotBalance(account.Type, balance);

        var existing = await _db.BalanceSnapshots
            .FirstOrDefaultAsync(s => s.AccountId == accountId && s.Date == date);

        if (existing is not null)
        {
            existing.Balance = normalizedBalance;
            await _db.SaveChangesAsync();
            return existing;
        }

        var snapshot = new BalanceSnapshot
        {
            AccountId = accountId,
            Date = date,
            Balance = normalizedBalance
        };
        _db.BalanceSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
        return snapshot;
    }

    public async Task<bool> DeleteSnapshotAsync(int snapshotId)
    {
        var snapshot = await _db.BalanceSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId);
        if (snapshot is null) return false;

        _db.BalanceSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync();
        return true;
    }
}
