using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Bounded write serialization for one SQLite database path. The in-process
/// semaphore serializes independent <see cref="SqliteUnitOfWork"/> instances in
/// this process; the named mutex serializes independent processes in the same user session;
/// the file lock serializes processes across session, account, and container boundaries.
/// All three are keyed by the same normalized path, so differently-cased or relative
/// spellings of one database cannot form separate gates.
///
/// Both the mutex and file lock are crash-safe: Windows abandons the mutex when the
/// owning process dies, and the next waiter takes ownership (see <see cref="AbandonedMutexException"/>);
/// the OS kernel automatically closes file handles and releases file locks on process termination.
/// SQLite then rolls back the dead process's uncommitted work before this writer
/// starts its own immediate transaction.
///
/// The lease is released exactly once by <see cref="DisposeAsync"/>. Callers must
/// release it on every exit path.
/// </summary>
internal sealed class SqliteWriteGate : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessGates =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _inProcessGate;
    private readonly NamedMutexLease _mutexLease;
    private readonly FileLockLease? _fileLockLease;
    private int _released;

    private SqliteWriteGate(
        SemaphoreSlim inProcessGate,
        NamedMutexLease mutexLease,
        FileLockLease? fileLockLease)
    {
        _inProcessGate = inProcessGate;
        _mutexLease = mutexLease;
        _fileLockLease = fileLockLease;
    }

    /// <summary>
    /// Acquires the per-path gate within <paramref name="timeoutMilliseconds"/>.
    /// On timeout or cancellation no gate is held and no transaction has started.
    /// </summary>
    public static async Task<SqliteWriteGate> AcquireAsync(
        string databasePath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var normalizedPath = NormalizePath(databasePath);
        var boundedTimeout = Math.Max(1, timeoutMilliseconds);
        var elapsed = Stopwatch.StartNew();

        var inProcessGate = InProcessGates.GetOrAdd(
            normalizedPath,
            static _ => new SemaphoreSlim(1, 1));

        if (!await inProcessGate.WaitAsync(boundedTimeout, cancellationToken))
        {
            throw new WriteGateTimeoutException(
                $"The per-database write gate was not acquired within {boundedTimeout} ms. " +
                $"No transaction was started and nothing was written. Database: '{normalizedPath}'.");
        }

        NamedMutexLease? mutexLease = null;
        FileLockLease? fileLockLease = null;

        try
        {
            // The in-process wait consumes part of the same bounded budget; the
            // cross-process wait may only use what is left.
            var remainingForMutex = boundedTimeout - (int)elapsed.ElapsedMilliseconds;
            if (remainingForMutex <= 0)
            {
                throw new WriteGateTimeoutException(
                    $"The per-database write gate was not acquired within {boundedTimeout} ms. " +
                    $"No transaction was started and nothing was written. Database: '{normalizedPath}'.");
            }

            mutexLease = await NamedMutexLease.AcquireAsync(
                BuildMutexName(normalizedPath),
                normalizedPath,
                remainingForMutex,
                cancellationToken);

            var remainingForFileLock = boundedTimeout - (int)elapsed.ElapsedMilliseconds;
            if (remainingForFileLock <= 0)
            {
                throw new WriteGateTimeoutException(
                    $"The per-database write gate was not acquired within {boundedTimeout} ms. " +
                    $"No transaction was started and nothing was written. Database: '{normalizedPath}'.");
            }

            fileLockLease = await FileLockLease.AcquireAsync(
                normalizedPath,
                remainingForFileLock,
                cancellationToken);

            return new SqliteWriteGate(inProcessGate, mutexLease, fileLockLease);
        }
        catch
        {
            if (fileLockLease is not null)
            {
                await fileLockLease.DisposeAsync();
            }

            if (mutexLease is not null)
            {
                await mutexLease.DisposeAsync();
            }

            inProcessGate.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                if (_fileLockLease is not null)
                {
                    await _fileLockLease.DisposeAsync();
                }
            }
            finally
            {
                // Release the cross-process gate so another process does not
                // queue behind this process's local semaphore for no reason.
                await _mutexLease.DisposeAsync();
            }
        }
        finally
        {
            _inProcessGate.Release();
        }
    }

    /// <summary>
    /// Full path (Windows: case-folded, no trailing separator) so every process
    /// and every connection string spelling maps to one gate. SQLite URI and
    /// in-memory identities are left intact; they are process-local by nature.
    /// </summary>
    internal static string NormalizePath(string databasePath)
    {
        if (databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return databasePath;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(databasePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Let SQLite report an invalid data source with its own error; the
            // gate still needs a stable key so it can be released.
            fullPath = databasePath;
        }

        fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    internal static string BuildMutexName(string normalizedDatabasePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDatabasePath));
        return PersistenceConstants.WriteGateMutexNamePrefix
            + Convert.ToHexString(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// A named mutex held by a dedicated owner thread for the lifetime of one
/// transaction. Windows mutex ownership is thread-affine, so the thread that
/// calls <c>WaitOne</c> must also call <c>ReleaseMutex</c>. Async continuations
/// cannot guarantee that, so acquisition and release both run on this thread.
/// </summary>
internal sealed class NamedMutexLease : IAsyncDisposable
{
    private readonly TaskCompletionSource<NamedMutexLease> _acquired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEventSlim _releaseRequested = new(false);
    private readonly Thread _ownerThread;
    private int _releaseStarted;

    private NamedMutexLease(
        string mutexName,
        string databasePath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        _ownerThread = new Thread(
            () => Run(mutexName, databasePath, timeoutMilliseconds, cancellationToken))
        {
            IsBackground = true,
            Name = "BoundlessStudy SQLite write gate",
        };
    }

    public static Task<NamedMutexLease> AcquireAsync(
        string mutexName,
        string databasePath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var lease = new NamedMutexLease(
            mutexName,
            databasePath,
            timeoutMilliseconds,
            cancellationToken);
        lease._ownerThread.Start();
        return lease._acquired.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _releaseStarted, 1) != 0)
        {
            await _released.Task;
            return;
        }

        _releaseRequested.Set();
        await _released.Task;
        _releaseRequested.Dispose();
    }

    private void Run(
        string mutexName,
        string databasePath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        Mutex? mutex = null;
        var ownsMutex = false;

        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            var elapsed = Stopwatch.StartNew();

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _acquired.TrySetCanceled(cancellationToken);
                    return;
                }

                var remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    _acquired.TrySetException(new WriteGateTimeoutException(
                        $"The per-database write gate was not acquired within " +
                        $"{timeoutMilliseconds} ms. No transaction was started and nothing " +
                        $"was written. Database: '{databasePath}'."));
                    return;
                }

                // Poll in short slices so cancellation stays bounded without
                // giving the wait an unbounded or cancellation-blind shape.
                var slice = Math.Min(remaining, 50);
                try
                {
                    if (mutex.WaitOne(slice))
                    {
                        ownsMutex = true;
                        _acquired.TrySetResult(this);
                        break;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous owner died without releasing. Ownership passes
                    // to us; SQLite rolls back its uncommitted transaction when
                    // this writer reads or begins its immediate transaction.
                    ownsMutex = true;
                    _acquired.TrySetResult(this);
                    break;
                }
            }

            _releaseRequested.Wait();
        }
        catch (Exception ex)
        {
            _acquired.TrySetException(ex);
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The thread no longer owns the mutex; nothing to release.
                }
            }

            mutex?.Dispose();
            _released.TrySetResult();
        }
    }
}

/// <summary>
/// A file lock lease on a dedicated lock file (&lt;database&gt;.write.lock) held with
/// <see cref="FileShare.None"/> for the lifetime of one write transaction.
/// This provides OS-level cross-process write serialization across different
/// user sessions, accounts, and platforms where named mutexes might be session-scoped.
/// OS file locks are crash-safe: the operating system automatically releases file locks
/// when a process dies or terminates.
/// </summary>
internal sealed class FileLockLease : IAsyncDisposable
{
    private readonly FileStream _lockStream;
    private int _disposed;

    private FileLockLease(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    public static async Task<FileLockLease?> AcquireAsync(
        string normalizedDatabasePath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (normalizedDatabasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || normalizedDatabasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lockFilePath = normalizedDatabasePath + ".write.lock";
        var lockDir = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
        {
            try
            {
                Directory.CreateDirectory(lockDir);
            }
            catch
            {
                // Let the FileStream open report path or permission errors.
            }
        }

        var deadline = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                return new FileLockLease(stream);
            }
            catch (IOException)
            {
                var remaining = timeoutMilliseconds - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    throw new WriteGateTimeoutException(
                        $"The per-database write lock file was not acquired within {timeoutMilliseconds} ms. " +
                        $"No transaction was started and nothing was written. Database: '{normalizedDatabasePath}'.");
                }

                attempt++;
                var delay = Math.Min(remaining, Math.Min(50, 10 + attempt * 5 + Random.Shared.Next(0, 10)));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lockStream.DisposeAsync();
    }
}
