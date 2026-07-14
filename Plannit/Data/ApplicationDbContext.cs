using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Plannit.Models.Entities;
using Plannit.Services;

namespace Plannit.Data;

public class ApplicationDbContext : IdentityDbContext
{
    private readonly ICacheVersionProvider? _cacheVersionProvider;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ICacheVersionProvider? cacheVersionProvider = null)
        : base(options)
    {
        _cacheVersionProvider = cacheVersionProvider;
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<BalanceSnapshot> BalanceSnapshots => Set<BalanceSnapshot>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();
    public DbSet<ProjectionScenario> ProjectionScenarios => Set<ProjectionScenario>();
    public DbSet<ProjectionAccountAssumption> ProjectionAccountAssumptions => Set<ProjectionAccountAssumption>();
    public DbSet<ProjectionEvent> ProjectionEvents => Set<ProjectionEvent>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<HoldingSnapshot> HoldingSnapshots => Set<HoldingSnapshot>();

    private string? _currentUserId;

    public void SetCurrentUser(string userId) => _currentUserId = userId;

    public string? CurrentUserId => _currentUserId;

    /// <summary>Current cache generation for the active user; bumped whenever a write touches
    /// a cache-affecting entity (see <see cref="SaveChangesAsync(CancellationToken)"/>).</summary>
    public int CacheVersion => _currentUserId is null ? 0 : (_cacheVersionProvider?.GetVersion(_currentUserId) ?? 0);

    public void BumpCacheVersion()
    {
        if (_currentUserId is not null)
            _cacheVersionProvider?.Bump(_currentUserId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampRowVersions();
        BumpCacheVersionIfNeeded();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampRowVersions();
        BumpCacheVersionIfNeeded();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Re-stamp the optimistic-concurrency token on every added/modified Account and
    // Transaction. This only sets the new (current) value; the Edit paths inject the
    // client's original token into OriginalValues so a stale save trips a
    // DbUpdateConcurrencyException. ExecuteUpdate/ExecuteDelete bypass this by design.
    private void StampRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Account>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.RowVersion = Guid.NewGuid();
        }
        foreach (var entry in ChangeTracker.Entries<Transaction>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.RowVersion = Guid.NewGuid();
        }
    }

    // Transactions and balance snapshots feed the cached net-worth/recurring-detection
    // aggregates; any tracked write to either invalidates this user's cache generation.
    // ExecuteUpdate/ExecuteDelete bypass the change tracker and call BumpCacheVersion() directly
    // at their call sites instead.
    private void BumpCacheVersionIfNeeded()
    {
        if (_currentUserId is null) return;

        var affectsCache = ChangeTracker.Entries<Transaction>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<BalanceSnapshot>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<Account>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

        if (affectsCache)
            _cacheVersionProvider?.Bump(_currentUserId);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Account>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(a => a.RowVersion).IsConcurrencyToken();
            e.Property(a => a.InterestRate).HasColumnType("decimal(6,4)");
            e.Property(a => a.MinimumPayment).HasColumnType("decimal(18,2)");
            e.Property(a => a.OriginalPrincipal).HasColumnType("decimal(18,2)");
            e.HasQueryFilter(a => _currentUserId != null && a.UserId == _currentUserId);
        });

        builder.Entity<BalanceSnapshot>(e =>
        {
            e.HasIndex(s => new { s.AccountId, s.Date }).IsUnique();
            e.Property(s => s.Balance).HasColumnType("decimal(18,2)");
            e.HasOne(s => s.Account).WithMany(a => a.Snapshots).HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => _currentUserId != null && s.Account.UserId == _currentUserId);
        });

        builder.Entity<Transaction>(e =>
        {
            e.HasIndex(t => t.ImportHash);
            e.HasIndex(t => t.OfxFitId);
            e.HasIndex(t => new { t.AccountId, t.Date });
            e.HasIndex(t => t.CategoryId);
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Category).WithMany(c => c.Transactions).HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.ImportBatch).WithMany(b => b.Transactions).HasForeignKey(t => t.ImportBatchId).OnDelete(DeleteBehavior.SetNull);
            e.Property(t => t.RowVersion).IsConcurrencyToken();
            e.HasQueryFilter(t => _currentUserId != null && t.Account.UserId == _currentUserId);
        });

        builder.Entity<ImportBatch>(e =>
        {
            e.HasOne(b => b.Account).WithMany().HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(b => _currentUserId != null && b.Account.UserId == _currentUserId);
        });

        builder.Entity<ImportProfile>(e =>
        {
            e.HasIndex(p => p.AccountId).IsUnique();
            e.HasOne(p => p.Account).WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => _currentUserId != null && p.Account.UserId == _currentUserId);
        });

        builder.Entity<Category>(e =>
        {
            e.HasIndex(c => c.UserId);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(c => _currentUserId != null && c.UserId == _currentUserId);
        });

        builder.Entity<CategoryRule>(e =>
        {
            e.HasIndex(r => r.UserId);
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Category).WithMany(c => c.Rules).HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => _currentUserId != null && r.UserId == _currentUserId);
        });

        builder.Entity<ProjectionScenario>(e =>
        {
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.AnnualRetirementSpending).HasColumnType("decimal(18,2)");
            e.Property(s => s.InflationRate).HasColumnType("decimal(5,4)");
            e.Property(s => s.ReturnStdDev).HasColumnType("decimal(5,4)");
            e.HasQueryFilter(s => _currentUserId != null && s.UserId == _currentUserId);
        });

        builder.Entity<ProjectionEvent>(e =>
        {
            e.HasIndex(ev => ev.ScenarioId);
            e.Property(ev => ev.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(ev => ev.Scenario).WithMany(s => s.Events).HasForeignKey(ev => ev.ScenarioId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(ev => _currentUserId != null && ev.Scenario.UserId == _currentUserId);
        });

        builder.Entity<ProjectionAccountAssumption>(e =>
        {
            e.HasIndex(a => new { a.ScenarioId, a.AccountId }).IsUnique();
            e.Property(a => a.AnnualContribution).HasColumnType("decimal(18,2)");
            e.Property(a => a.EmployerMatch).HasColumnType("decimal(18,2)");
            e.Property(a => a.ExpectedReturnRate).HasColumnType("decimal(5,4)");
            e.HasOne(a => a.Scenario).WithMany(s => s.AccountAssumptions).HasForeignKey(a => a.ScenarioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Account).WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(a => _currentUserId != null && a.Scenario.UserId == _currentUserId);
        });

        builder.Entity<Budget>(e =>
        {
            e.HasIndex(b => new { b.UserId, b.CategoryId }).IsUnique();
            e.Property(b => b.MonthlyAmount).HasColumnType("decimal(18,2)");
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.Category).WithMany().HasForeignKey(b => b.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(b => _currentUserId != null && b.UserId == _currentUserId);
        });

        builder.Entity<AiSettings>(e =>
        {
            e.HasIndex(s => s.UserId).IsUnique();
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => _currentUserId != null && s.UserId == _currentUserId);
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.Utc);
            // No FK to AspNetUsers: audit rows must survive account deletion, and some
            // events (e.g. a failed login for an unrecognized email) have no UserId at all.
            e.HasQueryFilter(a => _currentUserId != null && a.UserId == _currentUserId);
        });

        builder.Entity<Bill>(e =>
        {
            e.HasIndex(b => b.UserId);
            e.HasIndex(b => new { b.UserId, b.MerchantKey, b.IsIncome });
            e.HasOne(b => b.User).WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(b => b.ExpectedAmount).HasColumnType("decimal(18,2)");
            e.HasQueryFilter(b => _currentUserId != null && b.UserId == _currentUserId);
        });

        builder.Entity<SavingsGoal>(e =>
        {
            e.HasIndex(g => g.UserId);
            e.HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(g => g.LinkedAccount).WithMany().HasForeignKey(g => g.LinkedAccountId).OnDelete(DeleteBehavior.SetNull);
            e.Property(g => g.TargetAmount).HasColumnType("decimal(18,2)");
            e.Property(g => g.ManualProgress).HasColumnType("decimal(18,2)");
            e.HasQueryFilter(g => _currentUserId != null && g.UserId == _currentUserId);
        });

        builder.Entity<NotificationPreferences>(e =>
        {
            e.HasIndex(p => p.UserId).IsUnique();
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => _currentUserId != null && p.UserId == _currentUserId);
        });

        builder.Entity<Notification>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.DedupKey }).IsUnique();
            e.HasIndex(n => new { n.UserId, n.IsRead });
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(n => _currentUserId != null && n.UserId == _currentUserId);
        });

        builder.Entity<Holding>(e =>
        {
            e.HasIndex(h => new { h.AccountId, h.Symbol }).IsUnique();
            e.Property(h => h.Quantity).HasColumnType("decimal(18,6)");
            e.Property(h => h.CostBasis).HasColumnType("decimal(18,2)");
            e.HasOne(h => h.Account).WithMany().HasForeignKey(h => h.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(h => _currentUserId != null && h.Account.UserId == _currentUserId);
        });

        builder.Entity<HoldingSnapshot>(e =>
        {
            e.HasIndex(hs => new { hs.HoldingId, hs.Date }).IsUnique();
            e.Property(hs => hs.Quantity).HasColumnType("decimal(18,6)");
            e.Property(hs => hs.Price).HasColumnType("decimal(18,4)");
            e.Property(hs => hs.Value).HasColumnType("decimal(18,2)");
            e.HasOne(hs => hs.Holding).WithMany(h => h.Snapshots).HasForeignKey(hs => hs.HoldingId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(hs => _currentUserId != null && hs.Holding.Account.UserId == _currentUserId);
        });
    }
}
