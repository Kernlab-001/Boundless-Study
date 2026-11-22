namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// A stable database lifecycle failure. The agent maps these to the recovery/update
/// state instead of continuing with a schema it does not understand
/// (conceptual-data-model.md §8, R-P3-14, R-P3-16).
/// </summary>
public class PersistenceException : Exception
{
    public PersistenceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// The database schema and the compiled EF Core model do not match. Normal queries
/// must not run until this is resolved (R-P3-14).
/// </summary>
public sealed class SchemaMismatchException : PersistenceException
{
    public SchemaMismatchException(string message)
        : base("SCHEMA_VERSION_MISMATCH", message)
    {
    }
}

/// <summary>
/// A migration could not be started because a lock could not be proven stale.
/// The caller must surface an explicit recovery state rather than deleting data
/// or waiting without a bound (R-P3-16).
/// </summary>
public sealed class MigrationRecoveryRequiredException : PersistenceException
{
    public MigrationRecoveryRequiredException(string message, Exception? innerException = null)
        : base("MIGRATION_RECOVERY_REQUIRED", message, innerException)
    {
    }
}

/// <summary>Another bootstrapper held the per-database migration lock past the bound.</summary>
public sealed class MigrationLockTimeoutException : PersistenceException
{
    public MigrationLockTimeoutException(string message)
        : base("MIGRATION_LOCK_TIMEOUT", message)
    {
    }
}

/// <summary>
/// A write transaction could not acquire the per-database cross-process write
/// gate within <see cref="SqliteDatabaseOptions.BusyTimeoutMilliseconds"/>.
/// The gate is acquired before BEGIN IMMEDIATE, so no transaction was started and
/// nothing was written or partially upgraded. <c>WRITE_GATE_TIMEOUT</c> is a
/// stable, retryable code: the caller may retry the whole command; it must not
/// assume any part of the failed command committed (ADR-0005 write path).
/// </summary>
public sealed class WriteGateTimeoutException : PersistenceException
{
    public WriteGateTimeoutException(string message, Exception? innerException = null)
        : base("WRITE_GATE_TIMEOUT", message, innerException)
    {
    }
}
