using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Physical database locations and bounded-wait settings. The agent constructs
/// this; no other process is permitted to open the database (ADR-0005).
/// </summary>
public sealed record SqliteDatabaseOptions
{
    public required string DatabasePath { get; init; }

    /// <summary>Defaults to a sibling <c>Backups</c> folder next to the database.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>
    /// Bounds both SQLite's own busy wait and the per-database write-gate wait.
    /// The unit of work uses it as the single budget for acquiring the
    /// cross-process lock, so no writer waits without a bound (ADR-0005).
    /// </summary>
    public int BusyTimeoutMilliseconds { get; init; } = PersistenceConstants.BusyTimeoutMilliseconds;

    public int MigrationMutexWaitMilliseconds { get; init; } = PersistenceConstants.MigrationMutexWaitMilliseconds;

    public int MigrationLockProbeTimeoutMilliseconds { get; init; } = PersistenceConstants.MigrationLockProbeTimeoutMilliseconds;

    /// <summary>Pooling is disabled by temporary-file integration tests.</summary>
    public bool Pooling { get; init; } = true;

    public bool CreateBackupBeforeMigration { get; init; } = true;

    public int AutomaticBackupRetentionCount { get; init; } = PersistenceConstants.AutomaticBackupRetentionCount;

    public string ResolvedBackupDirectory =>
        BackupDirectory
        ?? Path.Combine(Path.GetDirectoryName(DatabasePath)!, PersistenceConstants.BackupFolderName);

    /// <summary>
    /// The per-user default: <c>%LocalAppData%\BoundlessStudy\boundless.db</c> with
    /// backups in <c>%LocalAppData%\BoundlessStudy\Backups</c>.
    /// </summary>
    public static SqliteDatabaseOptions ForCurrentUser()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PersistenceConstants.ProductFolderName);

        return new SqliteDatabaseOptions
        {
            DatabasePath = Path.Combine(root, PersistenceConstants.DatabaseFileName),
            BackupDirectory = Path.Combine(root, PersistenceConstants.BackupFolderName),
        };
    }

    public string BuildConnectionString() =>
        BuildConnectionString(BusyTimeoutMilliseconds);

    public string BuildConnectionString(int busyTimeoutMilliseconds)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = Pooling,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000d)),
        };

        return builder.ToString();
    }

    public DbContextOptions<BoundlessDbContext> BuildDbContextOptions() =>
        new DbContextOptionsBuilder<BoundlessDbContext>()
            .UseSqlite(BuildConnectionString(), sqlite => sqlite.MigrationsAssembly(typeof(BoundlessDbContext).Assembly.FullName))
            .AddInterceptors(new SqliteConnectionPragmaInterceptor(BusyTimeoutMilliseconds))
            .Options;

    /// <summary>
    /// Applies the required SQLite pragmas (foreign keys, synchronous, busy timeout)
    /// to an existing open or opening connection.
    /// </summary>
    public void ApplyPragmas(DbConnection connection) =>
        SqliteConnectionPragmaInterceptor.ApplyPragmas(connection, BusyTimeoutMilliseconds);

    /// <summary>
    /// Asynchronously applies the required SQLite pragmas to an existing open or opening connection.
    /// </summary>
    public Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken = default) =>
        SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(connection, BusyTimeoutMilliseconds, cancellationToken);
}
