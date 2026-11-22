using System.Diagnostics;
using BoundlessStudy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

public sealed record MigrationOutcome(
    bool DatabaseCreated,
    IReadOnlyList<string> PendingBeforeMigration,
    IReadOnlyList<string> AppliedMigrations,
    BackupArtifact? PreMigrationBackup,
    MigrationLockRecoveryResult? LockRecovery,
    string SchemaVersion);

/// <summary>
/// Runs code-first migrations inside the agent only (ADR-0005). It takes a
/// per-database file lock so two bootstrappers cannot migrate concurrently, makes a
/// verified online backup before a data-altering migration, recovers a provably
/// stale <c>__EFMigrationsLock</c> exactly once (R-P3-16), and verifies the schema
/// handshake before returning (R-P3-14).
/// </summary>
public sealed class SqliteMigrationRunner
{
    private readonly SqliteDatabaseOptions _options;
    private readonly DatabaseBackupService _backups;

    public SqliteMigrationRunner(
        SqliteDatabaseOptions options,
        DatabaseBackupService backups)
    {
        _options = options;
        _backups = backups;
    }

    public async Task<MigrationOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var databaseDirectory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(databaseDirectory))
        {
            DataDirectorySecurity.CreateWithOwnerOnlyAcl(databaseDirectory);
        }

        var databaseExisted = File.Exists(_options.DatabasePath);

        await using var lockFile = await AcquireFileLockAsync(cancellationToken);
        try
        {
            var pending = await GetPendingMigrationsAsync(cancellationToken);

            // A database carrying a migration this binary does not know (a newer or
            // divergent lineage) must be rejected before backup or MigrateAsync can
            // write anything. The post-migration handshake remains the final gate.
            await EnsureNoUnknownAppliedMigrationsAsync(cancellationToken);

            BackupArtifact? backup = null;
            MigrationLockRecoveryResult? lockRecovery = null;

            if (pending.Count > 0)
            {
                if (_options.CreateBackupBeforeMigration
                    && databaseExisted
                    && await HasUserTablesAsync(cancellationToken))
                {
                    await ConfigureWalAsync(cancellationToken);
                    var backupSchema = await CurrentAppliedSchemaVersionAsync(cancellationToken);
                    backup = await _backups.CreateAsync(
                        BackupReason.PreMigration,
                        backupSchema,
                        cancellationToken);
                }

                lockRecovery = await MigrationLockRecovery.ClearIfStaleAsync(_options, cancellationToken);
                if (lockRecovery.LiveWriterDetected)
                {
                    throw new MigrationRecoveryRequiredException(lockRecovery.Detail);
                }
            }

            try
            {
                await MigrateOnceAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                && pending.Count > 0
                && IsMigrationLockFailure(ex))
            {
                // The lock row may have appeared after the pre-flight probe (for
                // example after a crashed process). Prove it stale once, then retry.
                var retryRecovery = await MigrationLockRecovery.ClearIfStaleAsync(_options, cancellationToken);
                lockRecovery = retryRecovery;
                if (!retryRecovery.StaleLockCleared)
                {
                    throw new MigrationRecoveryRequiredException(
                        retryRecovery.Detail,
                        ex);
                }

                try
                {
                    await MigrateOnceAsync(cancellationToken);
                }
                catch (Exception retryEx) when (IsMigrationLockFailure(retryEx))
                {
                    throw new MigrationRecoveryRequiredException(
                        "The migration lock could not be acquired after safe recovery. No schema change was kept.",
                        retryEx);
                }
            }

            await ConfigureWalAsync(cancellationToken);

            await using var verification = new BoundlessDbContext(_options.BuildDbContextOptions());
            var handshake = await SchemaHandshake.VerifyAsync(verification, cancellationToken);

            return new MigrationOutcome(
                DatabaseCreated: !databaseExisted,
                PendingBeforeMigration: pending,
                AppliedMigrations: handshake.AppliedMigrations,
                PreMigrationBackup: backup,
                LockRecovery: lockRecovery,
                SchemaVersion: handshake.SchemaVersion);
        }
        finally
        {
            await lockFile.DisposeAsync();
        }
    }

    private async Task MigrateOnceAsync(CancellationToken cancellationToken)
    {
        await using var context = new BoundlessDbContext(_options.BuildDbContextOptions());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var context = new BoundlessDbContext(_options.BuildDbContextOptions());
        return (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
    }

    private async Task EnsureNoUnknownAppliedMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var context = new BoundlessDbContext(_options.BuildDbContextOptions());
        var compiled = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        var unknown = applied.Where(migration => !compiled.Contains(migration)).ToArray();

        if (unknown.Length > 0)
        {
            throw new SchemaMismatchException(
                "The database schema is newer than this application. Unknown migrations: " +
                string.Join(", ", unknown) + ". Install the newer application or restore a compatible backup.");
        }
    }

    private async Task<string> CurrentAppliedSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var context = new BoundlessDbContext(_options.BuildDbContextOptions());
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        return applied.Length == 0 ? "none" : applied[^1];
    }

    private async Task<bool> HasUserTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_options.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE '\\_\\_EF%' ESCAPE '\\';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task ConfigureWalAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_options.BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));

        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new PersistenceException(
                "WAL_UNAVAILABLE",
                $"SQLite did not enable WAL mode (reported '{mode ?? "null"}'); ADR-0005 requires WAL.");
        }
    }

    /// <summary>
    /// Bounded wait for another bootstrapper of the same database. The file is held
    /// with <see cref="FileShare.None"/> for the whole migration run.
    /// </summary>
    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = _options.DatabasePath + ".migration.lock";
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (deadline.ElapsedMilliseconds < _options.MigrationMutexWaitMilliseconds)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new MigrationLockTimeoutException(
                    $"Another process held the migration lock for more than " +
                    $"{_options.MigrationMutexWaitMilliseconds} ms. No migration was attempted. ({ex.Message})");
            }
        }
    }

    private static bool IsMigrationLockFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
            {
                return true;
            }

            if (current.Message.Contains("migration lock", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains(PersistenceConstants.MigrationsLockTable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
