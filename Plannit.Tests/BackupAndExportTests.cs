using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Plannit.Data;
using Plannit.Models.Entities;
using Plannit.Services;
using Plannit.Tests.Integration;

namespace Plannit.Tests;

/// <summary>
/// Regression tests for audit P2-05 (recoverable backups) and P2-06 (honest export). The shell
/// scripts and the container image are covered by scripts/test-backup.sh and
/// scripts/docker-backup-drill.sh, which run in CI.
/// </summary>
public class PreMigrationBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"plannit-premigrate-{Guid.NewGuid():N}");
    private string DbPath => Path.Combine(_dir, "plannit.db");

    public PreMigrationBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"DataSource={DbPath};Pooling=False").Options);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static string[] SnapshotsIn(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "pre-migrate_*.db") : [];

    /// <summary>Brings the database to the migration before the latest and seeds a marker row.</summary>
    private void MigrateToPreviousAndSeed()
    {
        using var db = NewContext();
        var all = db.Database.GetMigrations().ToList();
        db.GetService<IMigrator>().Migrate(all[^2]);
        db.Database.ExecuteSqlRaw("INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp) VALUES ('marker', 'BeforeMigration', 'BEFOREMIGRATION', 'x')");
    }

    [Fact]
    public void PendingMigration_TakesVerifiedSnapshot_BeforeMigrating()
    {
        MigrateToPreviousAndSeed();
        string? snapshot;
        using (var db = NewContext())
        {
            snapshot = PreMigrationBackup.CreateIfPending(db, Config(), NullLogger.Instance);
            db.Database.Migrate();
        }

        Assert.NotNull(snapshot);
        Assert.Equal(Path.Combine(_dir, "backups"), Path.GetDirectoryName(snapshot));
        Assert.Contains("_to_", Path.GetFileName(snapshot));

        // The snapshot is the pre-migration state: marker present, latest migration not yet applied.
        using var connection = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var marker = connection.CreateCommand();
        marker.CommandText = "SELECT count(*) FROM AspNetRoles WHERE Id = 'marker'";
        Assert.Equal(1L, marker.ExecuteScalar());
        using var history = connection.CreateCommand();
        history.CommandText = "SELECT count(*) FROM __EFMigrationsHistory";
        var snapshotMigrations = (long)history.ExecuteScalar()!;

        using var live = NewContext();
        Assert.Equal(live.Database.GetMigrations().Count() - 1, snapshotMigrations);
        Assert.Empty(live.Database.GetPendingMigrations());
    }

    [Fact]
    public void FreshDatabase_NothingToProtect()
    {
        using var db = NewContext();

        Assert.Null(PreMigrationBackup.CreateIfPending(db, Config(), NullLogger.Instance));
        Assert.Empty(SnapshotsIn(Path.Combine(_dir, "backups")));
    }

    [Fact]
    public void UpToDateDatabase_TakesNoSnapshot()
    {
        using (var db = NewContext()) db.Database.Migrate();
        using var again = NewContext();

        Assert.Null(PreMigrationBackup.CreateIfPending(again, Config(), NullLogger.Instance));
    }

    [Fact]
    public void DisabledByConfig_TakesNoSnapshot()
    {
        MigrateToPreviousAndSeed();
        using var db = NewContext();

        Assert.Null(PreMigrationBackup.CreateIfPending(db, Config(("Backup:PreMigration:Enabled", "false")), NullLogger.Instance));
    }

    [Fact]
    public void Snapshots_AreKeptInConfiguredDirectory_AndPruned()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(target);
        foreach (var old in new[] { "20200101_000000", "20200102_000000", "20200103_000000" })
            File.WriteAllText(Path.Combine(target, $"pre-migrate_{old}_to_X.db"), "old");
        MigrateToPreviousAndSeed();

        using var db = NewContext();
        var snapshot = PreMigrationBackup.CreateIfPending(db, Config(
            ("Backup:PreMigration:Directory", target), ("Backup:PreMigration:Keep", "2")), NullLogger.Instance);

        Assert.Equal(target, Path.GetDirectoryName(snapshot));
        var remaining = SnapshotsIn(target).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains(Path.GetFileName(snapshot), remaining);   // newest is kept
        Assert.DoesNotContain("pre-migrate_20200101_000000_to_X.db", remaining);
    }
}

public class PreMigrationStartupTests
{
    [Fact]
    public void AppStartup_SnapshotsAnOutdatedDatabase_BeforeMigrating()
    {
        var backups = Path.Combine(Path.GetTempPath(), $"plannit-startup-backups-{Guid.NewGuid():N}");
        using var factory = new PlannitWebAppFactory { Settings = { ["Backup:PreMigration:Directory"] = backups } };
        try
        {
            // An existing installation one migration behind, holding a marker row.
            using (var old = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                       .UseSqlite($"DataSource={factory.DatabasePath};Pooling=False").Options))
            {
                old.GetService<IMigrator>().Migrate(old.Database.GetMigrations().ToList()[^2]);
                old.Database.ExecuteSqlRaw("INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp) VALUES ('marker', 'Old', 'OLD', 'x')");
            }

            using var scope = factory.Services.CreateScope(); // boots the host: Program runs snapshot then Migrate

            var snapshot = Assert.Single(Directory.GetFiles(backups, "pre-migrate_*.db"));
            using var connection = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM AspNetRoles WHERE Id = 'marker'";
            Assert.Equal(1L, command.ExecuteScalar());
            Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetPendingMigrations());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(backups, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class KeyRingBackupTests
{
    [Fact]
    public void SecretsProtectedBeforeBackup_DecryptWithTheRestoredKeyRing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"plannit-keys-{Guid.NewGuid():N}");
        var original = Directory.CreateDirectory(Path.Combine(root, "original"));
        var restored = Path.Combine(root, "restored");
        try
        {
            string Protect(string keyDir, string plain) => DataProtectionProvider
                .Create(new DirectoryInfo(keyDir), o => o.SetApplicationName("Plannit"))
                .CreateProtector("Plannit.AiSettings.ApiKey").Protect(plain);
            string Unprotect(string keyDir, string cipher) => DataProtectionProvider
                .Create(new DirectoryInfo(keyDir), o => o.SetApplicationName("Plannit"))
                .CreateProtector("Plannit.AiSettings.ApiKey").Unprotect(cipher);

            var cipher = Protect(original.FullName, "synthetic-api-key");

            // What backup-db.sh / restore-db.sh do to the key ring: copy the directory intact.
            Directory.CreateDirectory(restored);
            foreach (var file in original.GetFiles())
                file.CopyTo(Path.Combine(restored, file.Name));

            Assert.NotEmpty(original.GetFiles());
            Assert.Equal("synthetic-api-key", Unprotect(restored, cipher));

            // Control: without the key ring the secret is unrecoverable, which is why it is backed up.
            var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Unprotect(empty, cipher));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class PartialExportTests : IDisposable
{
    private readonly PlannitWebAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient Client, string UserId)> SignedInAsync(string email, string accountName)
    {
        var client = _factory.CreateClientNoRedirect();
        await HttpTestHelpers.RegisterAsync(client, email);
        using var scope = _factory.Services.CreateScope();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email))!;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.SetCurrentUser(user.Id);
        db.Accounts.Add(new Account { UserId = user.Id, Name = accountName, Type = AccountType.Checking });
        await db.SaveChangesAsync();
        return (client, user.Id);
    }

    [Fact]
    public async Task Export_DeclaresItselfPartial_AndListsWhatIsMissing()
    {
        var (client, _) = await SignedInAsync("export-scope@example.invalid", "ExportMe");

        var response = await client.GetAsync("/Settings/ExportJson");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("plannit-partial-data-export", root.GetProperty("Kind").GetString());
        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Contains("not a complete backup", root.GetProperty("Scope").GetString());
        var missing = root.GetProperty("NotIncluded").EnumerateArray().Select(e => e.GetString()!).ToList();
        foreach (var expected in new[] { "holdings", "Bills", "Savings goals", "Loan and mortgage", "Bank sync", "AI provider" })
            Assert.Contains(missing, m => m.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(root.GetProperty("Limitations").EnumerateArray(), l => l.GetString()!.Contains("AccountId"));
        Assert.Contains("partial-export", response.Content.Headers.ContentDisposition!.FileName);
        Assert.DoesNotContain("backup", response.Content.Headers.ContentDisposition!.FileName!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_IsTenantScoped()
    {
        var (a, _) = await SignedInAsync("export-a@example.invalid", "AccountOfUserA");
        await SignedInAsync("export-b@example.invalid", "AccountOfUserB");

        var json = await (await a.GetAsync("/Settings/ExportJson")).Content.ReadAsStringAsync();

        Assert.Contains("AccountOfUserA", json);
        Assert.DoesNotContain("AccountOfUserB", json);
    }

    [Fact]
    public async Task SettingsPage_NoLongerCallsTheExportAFullBackup()
    {
        var (client, _) = await SignedInAsync("export-copy@example.invalid", "x");

        var html = await client.GetStringAsync("/Settings");

        Assert.Contains("not a complete backup", html);
        Assert.Contains("Partial", html);
        Assert.DoesNotContain("Full Backup", html);
        Assert.DoesNotContain("full backup", html);
    }
}
