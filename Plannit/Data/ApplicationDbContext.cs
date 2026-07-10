using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Plannit.Models.Entities;

namespace Plannit.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<BalanceSnapshot> BalanceSnapshots => Set<BalanceSnapshot>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();

    private string? _currentUserId;

    public void SetCurrentUser(string userId) => _currentUserId = userId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Account>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(a => _currentUserId == null || a.UserId == _currentUserId);
        });

        builder.Entity<BalanceSnapshot>(e =>
        {
            e.HasIndex(s => new { s.AccountId, s.Date }).IsUnique();
            e.Property(s => s.Balance).HasColumnType("decimal(18,2)");
            e.HasOne(s => s.Account).WithMany(a => a.Snapshots).HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Transaction>(e =>
        {
            e.HasIndex(t => t.ImportHash);
            e.HasIndex(t => new { t.AccountId, t.Date });
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)");
            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.ImportBatch).WithMany(b => b.Transactions).HasForeignKey(t => t.ImportBatchId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ImportBatch>(e =>
        {
            e.HasOne(b => b.Account).WithMany().HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ImportProfile>(e =>
        {
            e.HasIndex(p => p.AccountId).IsUnique();
            e.HasOne(p => p.Account).WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
