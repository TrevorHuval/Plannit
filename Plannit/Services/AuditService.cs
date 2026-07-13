using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

public class AuditService
{
    private readonly ApplicationDbContext _db;

    public AuditService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(string? userId, string action, string? detail = null, string? ip = null)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Action = action,
            Detail = detail,
            Utc = DateTime.UtcNow,
            Ip = ip
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<AuditEvent>> GetRecentAsync(DateOnly? startDate, DateOnly? endDate)
    {
        // The current-user query filter already scopes this to the caller's own events.
        var query = _db.AuditEvents.AsQueryable();

        if (startDate is not null)
        {
            var startUtc = startDate.Value.ToDateTime(TimeOnly.MinValue);
            query = query.Where(a => a.Utc >= startUtc);
        }

        if (endDate is not null)
        {
            var endUtc = endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
            query = query.Where(a => a.Utc < endUtc);
        }

        return await query.OrderByDescending(a => a.Utc).Take(500).ToListAsync();
    }

    /// <summary>
    /// Startup-only cross-user prune; intentionally bypasses the per-user query filter
    /// since it runs on a fresh, unauthenticated context with no current user set.
    /// </summary>
    public async Task<int> PruneOldAsync(TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        return await _db.AuditEvents
            .IgnoreQueryFilters()
            .Where(a => a.Utc < cutoff)
            .ExecuteDeleteAsync();
    }
}
