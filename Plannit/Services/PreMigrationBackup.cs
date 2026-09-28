using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plannit.Data;

namespace Plannit.Services;

/// <summary>
/// Takes a consistent snapshot of the SQLite database immediately before pending EF migrations run
/// (audit P2-05). A failed migration on a single-file database otherwise has no way back. The
/// snapshot is written with <c>VACUUM INTO</c>, which is safe while the file is open, verified with
/// an integrity check, and old snapshots are pruned. If a snapshot is required and cannot be made,
/// the exception stops startup before the schema is touched.
///
/// Config: <c>Backup:PreMigration:Enabled</c> (default true), <c>:Directory</c> (default
/// <c>backups</c> next to the database file), <c>:Keep</c> (default 5).
/// </summary>
public static class PreMigrationBackup
{
    public const int DefaultKeep = 5;

    /// <summary>Returns the snapshot path, or null when nothing needed backing up.</summary>
    public static string? CreateIfPending(ApplicationDbContext db, IConfiguration config, ILogger logger)
    {
        if (!config.GetValue("Backup:PreMigration:Enabled", true))
            return null;

        var pending = db.Database.GetPendingMigrations().ToList();
        // No pending work, or a brand-new database with nothing worth protecting.
        if (pending.Count == 0 || !db.Database.GetAppliedMigrations().Any())
            return null;

        var source = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(source) || source == ":memory:" || !File.Exists(source))
            return null;

        var directory = config["Backup:PreMigration:Directory"];
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "backups");
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, $"pre-migrate_{DateTime.UtcNow:yyyyMMdd_HHmmss}_to_{pending[^1]}.db");
        db.Database.ExecuteSqlRaw($"VACUUM INTO '{target.Replace("'", "''")}'");

        VerifyIntegrity(target);
        Prune(directory, Math.Max(1, config.GetValue("Backup:PreMigration:Keep", DefaultKeep)));

        logger.LogWarning("Took a pre-migration database snapshot before applying {Count} migration(s): {Path}", pending.Count, target);
        return target;
    }

    private static void VerifyIntegrity(string path)
    {
        string? result;
        using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            result = command.ExecuteScalar() as string;
        }

        if (result != "ok")
        {
            File.Delete(path);
            throw new InvalidOperationException($"The pre-migration snapshot failed its integrity check ({result}); migrations were not applied.");
        }
    }

    private static void Prune(string directory, int keep)
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles("pre-migrate_*.db")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                     .Skip(keep))
        {
            try { old.Delete(); } catch (IOException) { /* best effort */ }
        }
    }
}
