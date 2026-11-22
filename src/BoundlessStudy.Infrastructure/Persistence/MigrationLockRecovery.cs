using Microsoft.Data.Sqlite;

namespace BoundlessStudy.Infrastructure.Persistence;

public sealed record MigrationLockRecoveryResult(
    bool LockTableExists,
    bool LockRowFound,
    bool LiveWriterDetected,
    bool StaleLockCleared,
    string Detail);

/// <summary>
/// Safe recovery from a crashed EF Core migration (R-P3-16). A committed
/// <c>__EFMigrationsLock</c> row can survive a killed process. It is cleared only
/// inside a <c>BEGIN IMMEDIATE</c> transaction: acquiring SQLite's write lock proves
/// that no live writer is mid-migration, and the delete happens under that same
/// lock. If the lock cannot be acquired within the bounded timeout, recovery is
/// refused and the caller must stop.
/// </summary>
public static class MigrationLockRecovery
{
    public static async Task<MigrationLockRecoveryResult> ClearIfStaleAsync(
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        var connectionString = options.BuildConnectionString(
            options.MigrationLockProbeTimeoutMilliseconds);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, cancellationToken))
        {
            return new MigrationLockRecoveryResult(
                LockTableExists: false,
                LockRowFound: false,
                LiveWriterDetected: false,
                StaleLockCleared: false,
                Detail: "No migration lock table exists.");
        }

        var lockRows = await CountLockRowsAsync(connection, cancellationToken);
        if (lockRows == 0)
        {
            return new MigrationLockRecoveryResult(
                LockTableExists: true,
                LockRowFound: false,
                LiveWriterDetected: false,
                StaleLockCleared: false,
                Detail: "The migration lock table is empty.");
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"BEGIN IMMEDIATE; DELETE FROM \"{PersistenceConstants.MigrationsLockTable}\"; COMMIT;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return new MigrationLockRecoveryResult(
                LockTableExists: true,
                LockRowFound: true,
                LiveWriterDetected: true,
                StaleLockCleared: false,
                Detail: "A live writer still holds the database write lock; the stale lock was not cleared.");
        }

        var remaining = await CountLockRowsAsync(connection, cancellationToken);
        if (remaining != 0)
        {
            return new MigrationLockRecoveryResult(
                LockTableExists: true,
                LockRowFound: true,
                LiveWriterDetected: false,
                StaleLockCleared: false,
                Detail: "The stale lock row could not be removed.");
        }

        return new MigrationLockRecoveryResult(
            LockTableExists: true,
            LockRowFound: true,
            LiveWriterDetected: false,
            StaleLockCleared: true,
            Detail: "The migration lock row was proven stale and removed.");
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", PersistenceConstants.MigrationsLockTable);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    private static async Task<int> CountLockRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{PersistenceConstants.MigrationsLockTable}\";";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
