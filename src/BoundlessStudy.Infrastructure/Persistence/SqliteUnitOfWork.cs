using System.Data;
using System.Diagnostics;
using BoundlessStudy.Application.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// SQLite transaction boundary (§8.11). Nested begin/commit pairs are supported so
/// an agent command wrapper and a reward use case can share one outer transaction.
/// Only the outermost commit makes changes durable; every rollback discards the
/// whole transaction and detaches tracked state.
///
/// The outermost begin also acquires the bounded per-database write gate
/// (<see cref="SqliteWriteGate"/>) and starts a <c>BEGIN IMMEDIATE</c> transaction.
/// This serializes independent connections and processes before any read happens,
/// so a transaction can never read under a deferred lock and then fail while
/// upgrading to a writer. Nested begin/commit pairs only adjust depth; they never
/// acquire/release the gate or commit early.
/// </summary>
public sealed class SqliteUnitOfWork : IUnitOfWork, IAsyncDisposable
{
    private readonly BoundlessDbContext _context;
    private readonly int _writeGateTimeoutMilliseconds;
    private IDbContextTransaction? _transaction;
    private SqliteTransaction? _sqliteTransaction;
    private SqliteWriteGate? _writeGate;
    private bool _openedConnection;
    private int _depth;
    private bool _disposed;

    public SqliteUnitOfWork(BoundlessDbContext context)
        : this(context, PersistenceConstants.BusyTimeoutMilliseconds)
    {
    }

    /// <summary>
    /// The data store passes its configured <see cref="SqliteDatabaseOptions.BusyTimeoutMilliseconds"/>
    /// so tests and hosts can lower the bound without changing the public contract.
    /// </summary>
    internal SqliteUnitOfWork(BoundlessDbContext context, int writeGateTimeoutMilliseconds)
    {
        _context = context;
        _writeGateTimeoutMilliseconds = Math.Max(1, writeGateTimeoutMilliseconds);
    }

    public async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is not null)
        {
            _depth++;
            return;
        }

        var deadline = Stopwatch.StartNew();

        // The gate is taken before BEGIN IMMEDIATE, and only for the outermost
        // pair. If the begin fails below, the gate is released immediately.
        var gate = await SqliteWriteGate.AcquireAsync(
            _context.Database.GetDbConnection().DataSource,
            _writeGateTimeoutMilliseconds,
            cancellationToken);

        try
        {
            var remainingBudget = _writeGateTimeoutMilliseconds - (int)deadline.ElapsedMilliseconds;
            _transaction = await BeginImmediateTransactionWithRetryAsync(
                remainingBudget,
                cancellationToken);
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }

        _writeGate = gate;
        _depth = 1;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null)
        {
            throw new InvalidOperationException("There is no active transaction to commit.");
        }

        if (_depth > 1)
        {
            _depth--;
            return;
        }

        var transaction = _transaction;
        var sqliteTransaction = _sqliteTransaction;
        var gate = _writeGate;
        var openedHere = _openedConnection;

        // Reset the logical scope before any await so a cleanup failure cannot
        // leave a half-open outer transaction that a later Begin would reuse.
        _transaction = null;
        _sqliteTransaction = null;
        _writeGate = null;
        _openedConnection = false;
        _depth = 0;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await CommitWithRetryAsync(transaction, _writeGateTimeoutMilliseconds, cancellationToken);
        }
        catch
        {
            // A failed save/commit leaves the transaction uncommitted. Roll it
            // back explicitly; disposal below still guarantees the gate release.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original failure (the rollback state is unknown
                // but the connection/transaction are disposed below).
            }

            throw;
        }
        finally
        {
            await CompleteOutermostAsync(transaction, sqliteTransaction, openedHere, gate);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var transaction = _transaction;
        if (transaction is null)
        {
            _context.ChangeTracker.Clear();
            _depth = 0;
            await ReleaseStaleGateAsync();
            return;
        }

        var sqliteTransaction = _sqliteTransaction;
        var gate = _writeGate;
        var openedHere = _openedConnection;

        _transaction = null;
        _sqliteTransaction = null;
        _writeGate = null;
        _openedConnection = false;
        _depth = 0;

        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await CompleteOutermostAsync(transaction, sqliteTransaction, openedHere, gate);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var transaction = _transaction;
        var sqliteTransaction = _sqliteTransaction;
        var gate = _writeGate;
        var openedHere = _openedConnection;

        _transaction = null;
        _sqliteTransaction = null;
        _writeGate = null;
        _openedConnection = false;
        _depth = 0;

        if (transaction is null)
        {
            if (gate is not null)
            {
                await gate.DisposeAsync();
            }

            return;
        }

        try
        {
            await transaction.RollbackAsync();
        }
        finally
        {
            await CompleteOutermostAsync(transaction, sqliteTransaction, openedHere, gate);
        }
    }

    /// <summary>
    /// Starts SQLite's write reservation before the first read. If SQLite encounters
    /// a transient busy/locked error (e.g. from WAL checkpointing or contention), it
    /// retries with exponential backoff and jitter within the allotted timeout budget.
    /// </summary>
    private async Task<IDbContextTransaction> BeginImmediateTransactionWithRetryAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var boundedTimeout = Math.Max(100, timeoutMilliseconds);
        var deadline = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await BeginImmediateTransactionAsync(cancellationToken);
            }
            catch (Exception ex) when (IsTransientBusyException(ex))
            {
                var remaining = boundedTimeout - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    throw new WriteGateTimeoutException(
                        $"The immediate SQLite write transaction could not be started within {boundedTimeout} ms " +
                        $"due to persistent lock contention. Database: '{_context.Database.GetDbConnection().DataSource}'.",
                        ex);
                }

                attempt++;
                var delay = Math.Min(
                    remaining,
                    Math.Min(150, 15 + attempt * 15 + Random.Shared.Next(0, 20)));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Starts SQLite's write reservation before the first read. EF's
    /// <c>BeginTransactionAsync</c> always uses a deferred transaction, so the
    /// underlying connection starts an immediate transaction and EF is attached
    /// to it with <c>UseTransactionAsync</c>, keeping change tracking and
    /// <c>SaveChanges</c> unchanged.
    /// </summary>
    private async Task<IDbContextTransaction> BeginImmediateTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (_context.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException(
                "SqliteUnitOfWork requires the Microsoft.Data.Sqlite provider.");
        }

        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        await SqliteConnectionPragmaInterceptor.ApplyPragmasAsync(
            connection,
            _writeGateTimeoutMilliseconds,
            cancellationToken);

        SqliteTransaction? sqliteTransaction = null;
        IDbContextTransaction? efTransaction = null;

        try
        {
            sqliteTransaction = connection.BeginTransaction(deferred: false);
            efTransaction = await _context.Database.UseTransactionAsync(
                sqliteTransaction,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "EF Core did not accept the immediate SQLite transaction.");

            _sqliteTransaction = sqliteTransaction;
            _openedConnection = openedHere;
            return efTransaction;
        }
        catch
        {
            if (efTransaction is not null)
            {
                await efTransaction.DisposeAsync();
            }

            if (sqliteTransaction is not null)
            {
                await sqliteTransaction.DisposeAsync();
            }

            try
            {
                await _context.Database.CloseConnectionAsync();
            }
            catch
            {
            }

            throw;
        }
    }

    private static async Task CommitWithRetryAsync(
        IDbContextTransaction transaction,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (IsTransientBusyException(ex))
            {
                var remaining = timeoutMilliseconds - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    throw;
                }

                attempt++;
                var delay = Math.Min(
                    remaining,
                    Math.Min(150, 15 + attempt * 15 + Random.Shared.Next(0, 20)));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Inspects an exception and its inner causes for transient SQLite busy or locked conditions
    /// (e.g. SQLITE_BUSY [5], SQLITE_LOCKED [6], or SQLITE_IOERR [10] during process interruption cleanup).
    /// </summary>
    internal static bool IsTransientBusyException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                // 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED, 10 = SQLITE_IOERR (transient during OS lock cleanup)
                if (sqlite.SqliteErrorCode is 5 or 6 or 10)
                {
                    return true;
                }

                if (sqlite.SqliteExtendedErrorCode is 5 or 6 or 10 or 261 or 517 or 262 or 3850 or 4618 or 5130 or 5386)
                {
                    return true;
                }

                if (sqlite.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                    || sqlite.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)
                    || sqlite.Message.Contains("disk I/O error", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("SQLITE_LOCKED", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("disk I/O error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Disposes the EF wrapper and its underlying immediate transaction, closes
    /// the connection only if this unit of work opened it, and releases the gate
    /// exactly once. Each step is nested in <c>finally</c> so a disposal error
    /// cannot leak the cross-process lock.
    /// </summary>
    private async Task CompleteOutermostAsync(
        IDbContextTransaction transaction,
        SqliteTransaction? sqliteTransaction,
        bool openedHere,
        SqliteWriteGate? gate)
    {
        try
        {
            await transaction.DisposeAsync();
        }
        finally
        {
            try
            {
                if (sqliteTransaction is not null)
                {
                    await sqliteTransaction.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (openedHere)
                    {
                        await _context.Database.CloseConnectionAsync();
                    }
                }
                finally
                {
                    if (gate is not null)
                    {
                        await gate.DisposeAsync();
                    }
                }
            }
        }
    }

    private async Task ReleaseStaleGateAsync()
    {
        var staleGate = _writeGate;
        _writeGate = null;
        if (staleGate is not null)
        {
            await staleGate.DisposeAsync();
        }
    }
}
