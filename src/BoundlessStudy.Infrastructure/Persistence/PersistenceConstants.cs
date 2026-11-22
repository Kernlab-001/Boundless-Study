namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Fixed names and limits shared by the SQLite persistence layer
/// (conceptual-data-model.md §4-§8, ADR-0005).
/// </summary>
public static class PersistenceConstants
{
    public const string ProductFolderName = "BoundlessStudy";
    public const string DatabaseFileName = "boundless.db";
    public const string BackupFolderName = "Backups";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";
    public const string MigrationsLockTable = "__EFMigrationsLock";
    public const string AutomaticBackupFilePrefix = "boundless-backup-auto";
    public const string ManualBackupFilePrefix = "boundless-backup-manual";
    public const string PreMigrationBackupFilePrefix = "boundless-pre-migration";
    public const string PreRestoreBackupFilePrefix = "boundless-pre-restore";

    /// <summary>Bounded wait for SQLite write locks; ADR-0005 requires bounded retries.</summary>
    public const int BusyTimeoutMilliseconds = 5_000;

    /// <summary>
    /// Prefix of the named mutex that serializes writers for one database path.
    /// The suffix is a stable hash of the normalized full path; <c>Local\</c>
    /// keeps the object inside the per-user session (ADR-0005's agent).
    /// </summary>
    public const string WriteGateMutexNamePrefix = "Local\\BoundlessStudy.Sqlite.WriteGate.";

    /// <summary>Bounded wait for another bootstrapper in the same user session.</summary>
    public const int MigrationMutexWaitMilliseconds = 15_000;

    /// <summary>
    /// A live migration holds a write transaction. This short probe distinguishes a
    /// live writer from a stale committed <c>__EFMigrationsLock</c> row.
    /// </summary>
    public const int MigrationLockProbeTimeoutMilliseconds = 2_000;

    /// <summary>Conceptual-data-model.md §9: keep the seven most recent backups.</summary>
    public const int AutomaticBackupRetentionCount = 7;
}
