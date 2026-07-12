using Plannit.Models.Entities;

namespace Plannit.Services;

/// <summary>
/// Shared entry point for every import path that produces a balance snapshot
/// (OFX ledger balance, positions CSV, PDF statement) so they all normalize
/// and upsert through AccountService.AddSnapshotAsync consistently.
/// </summary>
public class SnapshotImportService
{
    private readonly AccountService _accountService;

    public SnapshotImportService(AccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<BalanceSnapshot?> UpsertSnapshotAsync(int accountId, DateOnly date, decimal balance)
    {
        return await _accountService.AddSnapshotAsync(accountId, date, balance);
    }
}
