using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services.Sync;

/// <summary>
/// Orchestrates automated bank sync (SimpleFIN): claims setup tokens, encrypts/decrypts the
/// access URL via Data Protection, discovers and maps provider accounts, and runs syncs that
/// route transactions through the same dedup/categorization pipeline as file imports and upsert
/// balances as snapshots. Every query goes through the tenancy filter, so all reads/writes are
/// scoped to the current user.
/// </summary>
public class SyncService
{
    private const string ProtectorPurpose = "Plannit.SyncConnection.AccessUrl";

    /// <summary>Feature flag gate for automated bank sync. Off by default (file import is the default path).</summary>
    public static bool IsFeatureEnabled(IConfiguration config) => config.GetValue("BankSync:Enabled", false);

    // How far back to request on the first sync when there's no prior watermark.
    private static readonly int InitialLookbackDays = 90;
    // Overlap re-requested on incremental syncs so a late-posting transaction isn't missed
    // (dedup drops the repeats).
    private static readonly int IncrementalOverlapDays = 5;

    private readonly ApplicationDbContext _db;
    private readonly SimpleFinClient _client;
    private readonly SnapshotImportService _snapshotImport;
    private readonly CategorizationService _categorization;
    private readonly IDataProtector _protector;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        ApplicationDbContext db,
        SimpleFinClient client,
        SnapshotImportService snapshotImport,
        CategorizationService categorization,
        IDataProtectionProvider dataProtection,
        ILogger<SyncService> logger)
    {
        _db = db;
        _client = client;
        _snapshotImport = snapshotImport;
        _categorization = categorization;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    // ===== Reads =====

    public async Task<List<SyncConnection>> GetConnectionsAsync() =>
        await _db.SyncConnections
            .Include(c => c.AccountMappings).ThenInclude(m => m.Account)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

    public async Task<SyncConnection?> GetConnectionAsync(int id) =>
        await _db.SyncConnections
            .Include(c => c.AccountMappings).ThenInclude(m => m.Account)
            .FirstOrDefaultAsync(c => c.Id == id);

    public async Task<List<SyncLog>> GetLogsAsync(int connectionId, int take = 50) =>
        await _db.SyncLogs
            .Where(l => l.SyncConnectionId == connectionId)
            .OrderByDescending(l => l.Utc)
            .Take(take)
            .ToListAsync();

    // ===== Connect / discover / map =====

    /// <summary>
    /// Redeems a setup token, stores the encrypted access URL as a new connection, then
    /// discovers the provider's accounts to seed mapping rows. Returns the new connection id.
    /// </summary>
    public async Task<(bool Ok, string Message, int? ConnectionId)> ConnectAsync(string userId, string setupToken)
    {
        string accessUrl;
        try
        {
            accessUrl = await _client.ClaimAccessUrlAsync(setupToken);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message, null);
        }
        catch (SimpleFinAuthException ex)
        {
            return (false, ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SimpleFIN claim failed for user {UserId}", userId);
            return (false, $"Could not claim the setup token: {ex.Message}", null);
        }

        var connection = new SyncConnection
        {
            UserId = userId,
            Provider = SyncProvider.SimpleFin,
            AccessUrlProtected = _protector.Protect(accessUrl)
        };
        _db.SyncConnections.Add(connection);
        await _db.SaveChangesAsync();

        // Best-effort account discovery — a transient fetch failure here shouldn't undo a
        // successfully claimed connection; the user can Refresh from the mapping screen.
        try
        {
            await RefreshAccountMappingsAsync(connection);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account discovery after claim failed for connection {ConnectionId}", connection.Id);
            return (true, "Connected, but couldn't list accounts yet — try Refresh on the mapping screen.", connection.Id);
        }

        return (true, "Bank connection linked. Map its accounts to continue.", connection.Id);
    }

    /// <summary>Fetches provider accounts and upserts a mapping row for each new external account (never removes rows).</summary>
    public async Task RefreshAccountMappingsAsync(SyncConnection connection)
    {
        var accessUrl = Decrypt(connection);
        var set = await _client.FetchAccountsAsync(accessUrl);

        var existing = await _db.SyncAccountMappings
            .Where(m => m.SyncConnectionId == connection.Id)
            .ToListAsync();

        foreach (var acct in set.Accounts)
        {
            if (string.IsNullOrEmpty(acct.Id)) continue;

            var mapping = existing.FirstOrDefault(m => m.ExternalAccountId == acct.Id);
            if (mapping is null)
            {
                _db.SyncAccountMappings.Add(new SyncAccountMapping
                {
                    SyncConnectionId = connection.Id,
                    ExternalAccountId = acct.Id,
                    ExternalAccountName = acct.Name,
                    ExternalOrgName = acct.OrgName
                });
            }
            else
            {
                // Keep display names fresh; don't touch the user's chosen AccountId.
                mapping.ExternalAccountName = acct.Name;
                mapping.ExternalOrgName = acct.OrgName;
            }
        }
    }

    /// <summary>Re-fetches the provider's accounts and persists any newly discovered mapping rows.
    /// Throws <see cref="SimpleFinAuthException"/> when the credentials are rejected.</summary>
    public async Task<bool> RefreshConnectionAccountsAsync(int connectionId)
    {
        var connection = await _db.SyncConnections.FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null) return false;

        await RefreshAccountMappingsAsync(connection);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Persists the external→Plannit account links. A null target unlinks that external account.</summary>
    public async Task<bool> SaveMappingsAsync(int connectionId, IDictionary<string, int?> externalToAccount)
    {
        var connection = await _db.SyncConnections.FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null) return false;

        var mappings = await _db.SyncAccountMappings
            .Where(m => m.SyncConnectionId == connectionId)
            .ToListAsync();

        foreach (var mapping in mappings)
        {
            if (!externalToAccount.TryGetValue(mapping.ExternalAccountId, out var accountId))
                continue;

            // Validate ownership: the target must resolve through the tenancy filter.
            if (accountId.HasValue && !await _db.Accounts.AnyAsync(a => a.Id == accountId.Value))
                accountId = null;

            mapping.AccountId = accountId;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetActiveAsync(int connectionId, bool active)
    {
        var connection = await _db.SyncConnections.FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null) return false;

        connection.IsActive = active;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteConnectionAsync(int connectionId)
    {
        var connection = await _db.SyncConnections.FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null) return false;

        _db.SyncConnections.Remove(connection);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Sync =====

    /// <summary>Syncs one connection by id (manual "Sync now"). Returns null if not found/owned.</summary>
    public async Task<SyncResult?> SyncNowAsync(int connectionId)
    {
        var connection = await _db.SyncConnections
            .Include(c => c.AccountMappings)
            .FirstOrDefaultAsync(c => c.Id == connectionId);
        if (connection is null) return null;

        return await SyncConnectionAsync(connection);
    }

    /// <summary>Syncs every active connection for the current user (background schedule). Returns runs completed.</summary>
    public async Task<int> SyncActiveConnectionsAsync(CancellationToken ct = default)
    {
        var connections = await _db.SyncConnections
            .Include(c => c.AccountMappings)
            .Where(c => c.IsActive)
            .ToListAsync(ct);

        int count = 0;
        foreach (var connection in connections)
        {
            if (ct.IsCancellationRequested) break;
            await SyncConnectionAsync(connection);
            count++;
        }
        return count;
    }

    private async Task<SyncResult> SyncConnectionAsync(SyncConnection connection)
    {
        var result = new SyncResult();

        string accessUrl;
        try
        {
            accessUrl = Decrypt(connection);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Access URL could not be decrypted for connection {ConnectionId} — data protection keys may have rotated", connection.Id);
            result.Message = "Stored credentials could not be read (encryption keys may have changed). Re-link the connection.";
            await FinalizeAsync(connection, result, SyncStatus.Failed);
            return result;
        }

        var startDate = connection.LastSyncedAt.HasValue
            ? DateOnly.FromDateTime(connection.LastSyncedAt.Value).AddDays(-IncrementalOverlapDays)
            : DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-InitialLookbackDays);

        SimpleFinAccountSet set;
        try
        {
            set = await _client.FetchAccountsAsync(accessUrl, startDate);
        }
        catch (SimpleFinAuthException ex)
        {
            result.TokenExpired = true;
            result.Message = ex.Message;
            await FinalizeAsync(connection, result, SyncStatus.TokenExpired);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync fetch failed for connection {ConnectionId}", connection.Id);
            result.Message = $"Could not reach SimpleFIN: {ex.Message}";
            await FinalizeAsync(connection, result, SyncStatus.Failed);
            return result;
        }

        var mapped = connection.AccountMappings
            .Where(m => m.AccountId.HasValue)
            .ToDictionary(m => m.ExternalAccountId, m => m.AccountId!.Value);

        foreach (var acct in set.Accounts)
        {
            if (!mapped.TryGetValue(acct.Id, out var accountId))
            {
                result.UnmappedAccounts++;
                continue;
            }

            result.AccountsSynced++;
            await ImportAccountAsync(accountId, acct, result);

            if (acct.Balance.HasValue && acct.BalanceDate.HasValue)
            {
                var snapshot = await _snapshotImport.UpsertSnapshotAsync(accountId, acct.BalanceDate.Value, acct.Balance.Value);
                if (snapshot is not null) result.SnapshotsUpdated++;
            }
        }

        // Categorize newly imported transactions in one pass, like file imports do.
        if (result.TransactionsImported > 0)
            await _categorization.ApplyRulesToUncategorizedAsync();

        var status = set.Errors.Count > 0 ? SyncStatus.PartialSuccess : SyncStatus.Success;
        result.Success = true;
        result.Message = BuildSummary(result, set.Errors);
        await FinalizeAsync(connection, result, status);
        return result;
    }

    /// <summary>
    /// Imports one provider account's transactions into a fresh <see cref="ImportBatch"/>, deduping
    /// on the SimpleFIN transaction id (strong key, stored in <c>OfxFitId</c>) with an ImportHash
    /// fallback — the same dedup contract the OFX importer uses.
    /// </summary>
    private async Task ImportAccountAsync(int accountId, SimpleFinAccount acct, SyncResult result)
    {
        if (acct.Transactions.Count == 0) return;

        var existingFitIds = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.OfxFitId != null)
            .Select(t => t.OfxFitId!)
            .ToHashSetAsync();

        var existingHashes = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.ImportHash != null)
            .Select(t => t.ImportHash!)
            .ToHashSetAsync();

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = $"SimpleFIN sync {DateTime.UtcNow:yyyy-MM-dd}",
            ImportedAt = DateTime.UtcNow
        };

        var toAdd = new List<Transaction>();

        foreach (var txn in acct.Transactions)
        {
            if (txn.Posted is null || txn.Amount is null) continue;

            if (!string.IsNullOrEmpty(txn.Id) && existingFitIds.Contains(txn.Id))
            {
                result.DuplicatesSkipped++;
                continue;
            }

            var description = txn.BestDescription;
            var hash = CsvImportService.ComputeImportHash(accountId, txn.Posted.Value, txn.Amount.Value, description);
            if (existingHashes.Contains(hash))
            {
                result.DuplicatesSkipped++;
                continue;
            }

            existingHashes.Add(hash);
            if (!string.IsNullOrEmpty(txn.Id)) existingFitIds.Add(txn.Id);

            toAdd.Add(new Transaction
            {
                AccountId = accountId,
                Date = txn.Posted.Value,
                Amount = txn.Amount.Value,
                Description = description,
                OriginalDescription = description,
                ImportHash = hash,
                OfxFitId = string.IsNullOrEmpty(txn.Id) ? null : txn.Id
            });
        }

        if (toAdd.Count == 0) return;

        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync();

        foreach (var t in toAdd) t.ImportBatchId = batch.Id;
        _db.Transactions.AddRange(toAdd);
        await _db.SaveChangesAsync();

        batch.RowCount = toAdd.Count;
        await _db.SaveChangesAsync();

        result.TransactionsImported += toAdd.Count;
    }

    private async Task FinalizeAsync(SyncConnection connection, SyncResult result, SyncStatus status)
    {
        connection.LastSyncedAt = DateTime.UtcNow;
        connection.LastSyncStatus = status;
        connection.LastSyncMessage = result.Message;

        _db.SyncLogs.Add(new SyncLog
        {
            SyncConnectionId = connection.Id,
            Utc = DateTime.UtcNow,
            Success = result.Success,
            TransactionsImported = result.TransactionsImported,
            DuplicatesSkipped = result.DuplicatesSkipped,
            SnapshotsUpdated = result.SnapshotsUpdated,
            Message = result.Message
        });

        await _db.SaveChangesAsync();
    }

    private static string BuildSummary(SyncResult r, List<string> errors)
    {
        var parts = new List<string>
        {
            $"{r.TransactionsImported} transaction(s) imported",
            $"{r.DuplicatesSkipped} duplicate(s) skipped",
            $"{r.SnapshotsUpdated} balance(s) updated"
        };
        if (r.UnmappedAccounts > 0) parts.Add($"{r.UnmappedAccounts} unmapped account(s) skipped");
        var summary = string.Join(", ", parts) + ".";
        if (errors.Count > 0)
            summary += " Provider reported: " + string.Join("; ", errors);
        return summary;
    }

    private string Decrypt(SyncConnection connection) => _protector.Unprotect(connection.AccessUrlProtected);
}
