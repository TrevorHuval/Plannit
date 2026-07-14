using Microsoft.EntityFrameworkCore;
using Plannit.Data;

namespace Plannit.Services;

/// <summary>
/// Daily housekeeping: sweeps stale temp upload files, prunes old audit events, and runs the
/// per-user notification alert engine. Runs once shortly after startup and then on a fixed
/// interval — home for future scheduled work too.
/// </summary>
public class MaintenanceBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly TimeSpan TempFileMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan AuditRetention = TimeSpan.FromDays(90);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MaintenanceBackgroundService> _logger;

    public MaintenanceBackgroundService(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, ILogger<MaintenanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        CleanupOldTempUploads();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var auditService = scope.ServiceProvider.GetRequiredService<AuditService>();
            var pruned = await auditService.PruneOldAsync(AuditRetention);
            if (pruned > 0)
                _logger.LogInformation("Pruned {Count} audit event(s) older than {Days} days.", pruned, AuditRetention.TotalDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit event pruning failed.");
        }

        await RunNotificationChecksAsync(ct);
    }

    private async Task RunNotificationChecksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var userIds = await db.Users.Select(u => u.Id).ToListAsync(ct);
        foreach (var userId in userIds)
        {
            try
            {
                db.SetCurrentUser(userId);
                var created = await notificationService.RunDailyChecksAsync(userId, ct);
                if (created > 0)
                    _logger.LogInformation("Created {Count} notification(s) for user {UserId}.", created, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification checks failed for user {UserId}.", userId);
            }
        }
    }

    private void CleanupOldTempUploads()
    {
        var tempDir = Path.Combine(_env.ContentRootPath, "TempUploads");
        if (!Directory.Exists(tempDir)) return;

        var cutoff = DateTime.UtcNow - TempFileMaxAge;
        var deleted = 0;
        foreach (var file in Directory.GetFiles(tempDir))
        {
            if (File.GetCreationTimeUtc(file) >= cutoff) continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete stale temp upload file {Path}", file);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Deleted {Count} stale temp upload file(s).", deleted);
    }
}
