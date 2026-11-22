using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace BoundlessStudy.Agent;

/// <summary>
/// Abstraction for executing recovery sweeps.
/// </summary>
public interface IRecoverySweepExecutor
{
    Task<PendingRecoverySweepResult?> ExecuteSweepAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes recovery sweeps against SQLite with robust error handling, database
/// lock protection, and transactional idempotency.
/// </summary>
public sealed class RecoverySweepExecutor : IRecoverySweepExecutor
{
    private readonly SqliteDataStore _store;
    private readonly ILocalClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecoverySweepExecutor(SqliteDataStore store, ILocalClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Executes a single sweep pass. Returns the result on success, or null if
    /// the sweep was deferred or skipped due to a database lock or concurrent execution.
    /// </summary>
    public async Task<PendingRecoverySweepResult?> ExecuteSweepAsync(CancellationToken cancellationToken = default)
    {
        // Prevent concurrent sweeps within this process (e.g. startup sweep overlapping a timer tick)
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            Console.WriteLine("BoundlessStudy.Agent: sweep deferred: another sweep is currently executing.");
            return null;
        }

        try
        {
            await using var repos = _store.CreateRepositories();
            var sweep = PendingRecoverySweep.Create(
                repos.LocalProfiles,
                repos.FocusSessions,
                repos.UnitOfWork,
                repos.RewardLedger,
                repos.Achievements,
                repos.UserAchievements,
                repos.RewardState,
                repos.SessionEvents,
                _clock);

            return await sweep.SweepAsync(cancellationToken);
        }
        catch (WriteGateTimeoutException ex)
        {
            // The per-database write gate was held by another operation (e.g. desktop UI or backup)
            Console.Error.WriteLine($"BoundlessStudy.Agent: sweep deferred: write gate busy ({ex.Message})");
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */)
        {
            Console.Error.WriteLine($"BoundlessStudy.Agent: sweep deferred: SQLite database busy/locked (code {ex.SqliteErrorCode})");
            return null;
        }
        catch (PersistenceException ex)
        {
            Console.Error.WriteLine($"BoundlessStudy.Agent: sweep deferred: persistence error ({ex.Code}: {ex.Message})");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Catch-all for unexpected transient failures so the background process never crashes
            Console.Error.WriteLine($"BoundlessStudy.Agent: sweep unexpected error ({ex.GetType().Name}): {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
