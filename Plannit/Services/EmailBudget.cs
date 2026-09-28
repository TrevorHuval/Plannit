using Microsoft.EntityFrameworkCore;
using Plannit.Data;
using Plannit.Models.Entities;

namespace Plannit.Services;

/// <summary>
/// Send budgets for outbound email (audit P2-08). A send must be reserved here first. Limits, all
/// over a rolling 24 hours unless noted, configurable under <c>Email:</c>:
/// <list type="bullet">
/// <item><c>UserDailyLimit</c> (5) verification/test mails a user may trigger, plus
/// <c>UserCooldownSeconds</c> (60) between them.</item>
/// <item><c>AlertDailyLimit</c> (20) notification alert mails per user.</item>
/// <item><c>GlobalDailyLimit</c> (500) mails across every user, so one busy or abusive deployment
/// cannot exhaust the SMTP provider's quota or reputation.</item>
/// </list>
/// The reservation is written before the send and counted afterwards, so concurrent requests can
/// only be refused too eagerly, never over-admitted. Counting never relies on the tenancy filter.
/// </summary>
public class EmailBudget
{
    public const string UserTriggered = "UserTriggered";
    public const string Alert = "Alert";

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;

    public EmailBudget(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<(bool Allowed, string? Reason)> TryReserveAsync(string userId, string kind, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-24);

        var userLimit = kind == UserTriggered
            ? Math.Max(1, _config.GetValue("Email:UserDailyLimit", 5))
            : Math.Max(1, _config.GetValue("Email:AlertDailyLimit", 20));
        var globalLimit = Math.Max(1, _config.GetValue("Email:GlobalDailyLimit", 500));

        if (kind == UserTriggered)
        {
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _config.GetValue("Email:UserCooldownSeconds", 60)));
            var since = now - cooldown;
            var recent = await _db.EmailDispatches.IgnoreQueryFilters()
                .AnyAsync(d => d.UserId == userId && d.Kind == UserTriggered && d.SentUtc > since, ct);
            if (recent)
                return (false, $"Please wait {(int)Math.Ceiling(cooldown.TotalSeconds)} seconds before requesting another email.");
        }

        var reservation = new EmailDispatch { UserId = userId, Kind = kind, SentUtc = now };
        _db.EmailDispatches.Add(reservation);
        await _db.SaveChangesAsync(ct);

        var userCount = await _db.EmailDispatches.IgnoreQueryFilters()
            .CountAsync(d => d.UserId == userId && d.Kind == kind && d.SentUtc > windowStart, ct);
        var globalCount = await _db.EmailDispatches.IgnoreQueryFilters()
            .CountAsync(d => d.SentUtc > windowStart, ct);

        if (userCount > userLimit)
        {
            await ReleaseAsync(reservation, ct);
            return (false, kind == UserTriggered
                ? "You have reached today's limit for verification and test emails. Try again tomorrow."
                : "Daily alert email limit reached.");
        }

        if (globalCount > globalLimit)
        {
            await ReleaseAsync(reservation, ct);
            return (false, "This server has reached its daily email limit. Try again tomorrow.");
        }

        return (true, null);
    }

    /// <summary>Deletes rows older than the counting window; called from the daily sweep.</summary>
    public Task<int> PruneAsync(CancellationToken ct = default) =>
        _db.EmailDispatches.IgnoreQueryFilters()
            .Where(d => d.SentUtc < DateTime.UtcNow.AddDays(-2))
            .ExecuteDeleteAsync(ct);

    private async Task ReleaseAsync(EmailDispatch reservation, CancellationToken ct)
    {
        _db.EmailDispatches.Remove(reservation);
        await _db.SaveChangesAsync(ct);
    }
}
