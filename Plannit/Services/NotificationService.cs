using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

/// <summary>
/// In-app notification center plus the daily alert engine: budget overage, bills due soon,
/// a negative cash-flow forecast, stale account snapshots, and (triggered from the import
/// pipeline rather than the daily loop) unusually large transactions. Every alert is deduped
/// on a per-condition key so a state that's still true the next day doesn't re-notify.
/// </summary>
public class NotificationService
{
    private const decimal LargeTransactionMultiplier = 3m;
    private const int LargeTransactionMinHistory = 3;
    private const int BillDueLookaheadDays = 3;
    private const int StaleAccountThresholdDays = 30;
    private const int ForecastLookaheadDays = 30;

    private readonly ApplicationDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly BudgetService _budgetService;
    private readonly BillService _billService;
    private readonly ForecastService _forecastService;
    private readonly NetWorthService _netWorthService;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        ApplicationDbContext db,
        IEmailSender emailSender,
        BudgetService budgetService,
        BillService billService,
        ForecastService forecastService,
        NetWorthService netWorthService,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _budgetService = budgetService;
        _billService = billService;
        _forecastService = forecastService;
        _netWorthService = netWorthService;
        _logger = logger;
    }

    // ===== Preferences =====

    /// <summary>Returns the stored row, or in-memory defaults (all alert types on, email off) if the user never saved any.</summary>
    public async Task<NotificationPreferences> GetPreferencesAsync(string userId, CancellationToken ct = default)
    {
        var existing = await _db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(ct);
        return existing ?? new NotificationPreferences { UserId = userId };
    }

    public async Task<NotificationPreferences> SavePreferencesAsync(
        string userId, string? email, bool emailEnabled, NotificationDigestMode digestMode,
        bool budgetOverage, bool billDue, bool lowForecastBalance, bool largeTransaction, bool staleAccount)
    {
        var prefs = await _db.NotificationPreferences.FirstOrDefaultAsync();
        if (prefs is null)
        {
            prefs = new NotificationPreferences { UserId = userId };
            _db.NotificationPreferences.Add(prefs);
        }

        prefs.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        prefs.EmailEnabled = emailEnabled;
        prefs.DigestMode = digestMode;
        prefs.BudgetOverageEnabled = budgetOverage;
        prefs.BillDueEnabled = billDue;
        prefs.LowForecastBalanceEnabled = lowForecastBalance;
        prefs.LargeTransactionEnabled = largeTransaction;
        prefs.StaleAccountEnabled = staleAccount;

        await _db.SaveChangesAsync();
        return prefs;
    }

    public async Task<(bool Ok, string Message)> SendTestEmailAsync(string toEmail)
    {
        if (!_emailSender.IsConfigured)
            return (false, "SMTP is not configured on this server. Set Smtp:Enabled and the connection details in configuration.");

        try
        {
            await _emailSender.SendAsync(toEmail, "Plannit test email", "This is a test email from Plannit's notification settings. If you received this, SMTP is working.");
            return (true, $"Test email sent to {toEmail}.");
        }
        catch (Exception ex)
        {
            return (false, $"Send failed: {ex.Message}");
        }
    }

    // ===== In-app notification center =====

    public async Task<List<Notification>> GetRecentAsync(int count = 20) =>
        await _db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedUtc).Take(count).ToListAsync();

    public Task<int> GetUnreadCountAsync() =>
        _db.Notifications.CountAsync(n => !n.IsRead);

    public async Task<bool> MarkReadAsync(int id)
    {
        var notification = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id);
        if (notification is null) return false;
        notification.IsRead = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task MarkAllReadAsync()
    {
        await _db.Notifications.Where(n => !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }

    // ===== Daily alert engine (called once per user from MaintenanceBackgroundService) =====

    public async Task<int> RunDailyChecksAsync(string userId, CancellationToken ct = default)
    {
        var prefs = await GetPreferencesAsync(userId, ct);
        var created = new List<Notification>();

        if (prefs.BudgetOverageEnabled) created.AddRange(await CheckBudgetOverageAsync(userId, ct));
        if (prefs.BillDueEnabled) created.AddRange(await CheckBillsDueAsync(userId, ct));
        if (prefs.LowForecastBalanceEnabled) created.AddRange(await CheckLowForecastBalanceAsync(userId, ct));
        if (prefs.StaleAccountEnabled) created.AddRange(await CheckStaleAccountsAsync(userId, ct));

        if (created.Count > 0)
        {
            _db.Notifications.AddRange(created);
            await _db.SaveChangesAsync(ct);

            if (prefs.DigestMode == NotificationDigestMode.Immediate)
                await SendImmediateEmailsAsync(prefs, created, ct);
        }

        if (prefs.DigestMode == NotificationDigestMode.DailyDigest)
            await SendDigestIfDueAsync(prefs, ct);

        return created.Count;
    }

    private async Task<List<Notification>> CheckBudgetOverageAsync(string userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var statuses = await _budgetService.GetBudgetStatusAsync(today);
        var monthKey = $"{today.Year:D4}-{today.Month:D2}";

        var result = new List<Notification>();
        foreach (var status in statuses.Where(s => s.Percentage >= 1m))
        {
            var dedupKey = $"BudgetOverage:{status.Budget.CategoryId}:{monthKey}";
            if (await _db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) continue;

            result.Add(new Notification
            {
                UserId = userId,
                Type = NotificationType.BudgetOverage,
                Title = $"{status.Budget.Category.Name} budget exceeded",
                Message = $"You've spent {status.Spent:C} of your {status.Budget.MonthlyAmount:C} {status.Budget.Category.Name} budget this month ({status.Percentage:P0}).",
                Url = "/Budgets",
                DedupKey = dedupKey,
                CreatedUtc = DateTime.UtcNow
            });
        }
        return result;
    }

    private async Task<List<Notification>> CheckBillsDueAsync(string userId, CancellationToken ct)
    {
        var upcoming = await _billService.GetUpcomingAsync(BillDueLookaheadDays);

        var result = new List<Notification>();
        foreach (var bill in upcoming)
        {
            var dedupKey = $"BillDue:{bill.Id}:{bill.NextDue:yyyy-MM-dd}";
            if (await _db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) continue;

            result.Add(new Notification
            {
                UserId = userId,
                Type = NotificationType.BillDue,
                Title = $"{bill.Name} due soon",
                Message = $"{bill.Name} ({bill.ExpectedAmount:C}) is due {bill.NextDue:MMM d}.",
                Url = "/Bills",
                DedupKey = dedupKey,
                CreatedUtc = DateTime.UtcNow
            });
        }
        return result;
    }

    private async Task<List<Notification>> CheckLowForecastBalanceAsync(string userId, CancellationToken ct)
    {
        var forecast = await _forecastService.GetForecastAsync(ForecastLookaheadDays);
        if (forecast.ZeroCrossingDate is not { } crossing) return [];

        var dedupKey = $"LowForecastBalance:{crossing:yyyy-MM-dd}";
        if (await _db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) return [];

        return
        [
            new Notification
            {
                UserId = userId,
                Type = NotificationType.LowForecastBalance,
                Title = "Cash flow forecast turns negative",
                Message = $"Based on upcoming bills and your average spending, your checking/savings balance is projected to go negative on {crossing:MMM d}.",
                Url = "/Bills",
                DedupKey = dedupKey,
                CreatedUtc = DateTime.UtcNow
            }
        ];
    }

    private async Task<List<Notification>> CheckStaleAccountsAsync(string userId, CancellationToken ct)
    {
        var stale = await _netWorthService.GetStaleAccountsAsync(StaleAccountThresholdDays);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var result = new List<Notification>();
        foreach (var account in stale)
        {
            // Reconstructs the account's last-updated date from DaysSinceUpdate so the dedup key
            // changes once a fresh snapshot lands, allowing a future staleness episode to re-alert.
            var anchor = today.AddDays(-account.DaysSinceUpdate);
            var dedupKey = $"StaleAccount:{account.AccountId}:{anchor:yyyy-MM-dd}";
            if (await _db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) continue;

            result.Add(new Notification
            {
                UserId = userId,
                Type = NotificationType.StaleAccount,
                Title = $"{account.AccountName} balance is stale",
                Message = $"{account.AccountName} hasn't had a balance update in {account.DaysSinceUpdate} days.",
                Url = "/Accounts",
                DedupKey = dedupKey,
                CreatedUtc = DateTime.UtcNow
            });
        }
        return result;
    }

    // ===== Import-time anomaly check (called synchronously from ImportWorkflowService) =====

    /// <summary>
    /// Flags newly-imported expense transactions whose amount is well above the historical
    /// average for their category. Relies on the request-scoped ApplicationDbContext already
    /// having a current user set (via the auth middleware), since the import pipeline doesn't
    /// otherwise thread a user id through to here.
    /// </summary>
    public async Task<int> CheckImportAnomaliesAsync(int importBatchId, CancellationToken ct = default)
    {
        var userId = _db.CurrentUserId;
        if (userId is null) return 0;

        var prefs = await GetPreferencesAsync(userId, ct);
        if (!prefs.LargeTransactionEnabled) return 0;

        var newTxns = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.ImportBatchId == importBatchId && t.Amount < 0 && t.CategoryId != null)
            .Select(t => new { t.Id, t.Amount, t.CategoryId, t.Description, t.Date })
            .ToListAsync(ct);
        if (newTxns.Count == 0) return 0;

        var categoryStats = await _db.Transactions
            .AsNoTracking()
            .Where(t => t.Amount < 0 && t.CategoryId != null && t.ImportBatchId != importBatchId)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Avg = g.Average(t => -t.Amount), Count = g.Count() })
            .ToDictionaryAsync(g => g.CategoryId, g => g, ct);

        var created = new List<Notification>();
        foreach (var t in newTxns)
        {
            if (!categoryStats.TryGetValue(t.CategoryId!.Value, out var stats) || stats.Count < LargeTransactionMinHistory)
                continue;

            var magnitude = -t.Amount;
            if (magnitude <= stats.Avg * LargeTransactionMultiplier) continue;

            var dedupKey = $"LargeTransaction:{t.Id}";
            if (await _db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) continue;

            created.Add(new Notification
            {
                UserId = userId,
                Type = NotificationType.LargeTransaction,
                Title = "Unusually large transaction",
                Message = $"{t.Description}: {magnitude:C} on {t.Date:MMM d} is over {LargeTransactionMultiplier:0}x your average for this category ({stats.Avg:C}).",
                Url = "/Transactions",
                DedupKey = dedupKey,
                CreatedUtc = DateTime.UtcNow
            });
        }

        if (created.Count == 0) return 0;

        _db.Notifications.AddRange(created);
        await _db.SaveChangesAsync(ct);

        if (prefs.DigestMode == NotificationDigestMode.Immediate)
            await SendImmediateEmailsAsync(prefs, created, ct);

        return created.Count;
    }

    // ===== Email delivery =====

    private async Task SendImmediateEmailsAsync(NotificationPreferences prefs, List<Notification> notifications, CancellationToken ct)
    {
        if (!prefs.EmailEnabled || !_emailSender.IsConfigured || string.IsNullOrWhiteSpace(prefs.Email)) return;

        var anySent = false;
        foreach (var n in notifications)
        {
            try
            {
                await _emailSender.SendAsync(prefs.Email!, n.Title, n.Message, ct);
                n.EmailSent = true;
                anySent = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification email to {Email}", prefs.Email);
            }
        }

        if (anySent) await _db.SaveChangesAsync(ct);
    }

    // Bundles every not-yet-emailed notification into a single digest — covers both alerts
    // created by today's daily run and any import-time alerts created earlier in the day.
    private async Task SendDigestIfDueAsync(NotificationPreferences prefs, CancellationToken ct)
    {
        if (!prefs.EmailEnabled || !_emailSender.IsConfigured || string.IsNullOrWhiteSpace(prefs.Email)) return;

        var pending = await _db.Notifications.Where(n => !n.EmailSent).OrderBy(n => n.CreatedUtc).ToListAsync(ct);
        if (pending.Count == 0) return;

        var body = string.Join("\n\n", pending.Select(n => $"{n.Title}\n{n.Message}"));
        try
        {
            await _emailSender.SendAsync(prefs.Email!, $"Plannit: {pending.Count} new alert(s)", body, ct);
            foreach (var n in pending) n.EmailSent = true;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send digest email to {Email}", prefs.Email);
        }
    }
}
