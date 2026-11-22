using BoundlessStudy.Application.UseCases;

namespace BoundlessStudy.Agent;

/// <summary>
/// Contract for the agent's background recovery worker.
/// </summary>
public interface IBackgroundRecoveryWorker : IAsyncDisposable
{
    Task<PendingRecoverySweepResult?> RunStartupSweepAsync(CancellationToken cancellationToken = default);
    void Start();
    Task RunAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

/// <summary>
/// Background worker that coordinates startup and periodic background sweeps
/// for expiring abandoned focus sessions without user interaction.
/// </summary>
public sealed class BackgroundRecoveryWorker : IBackgroundRecoveryWorker
{
    private readonly IRecoverySweepExecutor _executor;
    private readonly AgentRecoveryOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runningLoop;
    private int _started;
    private bool _disposed;

    public BackgroundRecoveryWorker(IRecoverySweepExecutor executor, AgentRecoveryOptions? options = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? new AgentRecoveryOptions();
    }

    /// <summary>
    /// Executes the startup sweep immediately and reports the outcome.
    /// </summary>
    public async Task<PendingRecoverySweepResult?> RunStartupSweepAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("BoundlessStudy.Agent: running startup recovery sweep...");
        var result = await _executor.ExecuteSweepAsync(cancellationToken);
        if (result is null)
        {
            Console.WriteLine("BoundlessStudy.Agent: startup recovery sweep deferred (database busy).");
            return null;
        }

        if (result.SessionsAbandoned > 0)
        {
            Console.WriteLine(
                $"BoundlessStudy.Agent: startup recovery sweep abandoned {result.SessionsAbandoned} " +
                $"expired session(s): [{string.Join(", ", result.AbandonedSessionIds)}].");
        }
        else
        {
            Console.WriteLine("BoundlessStudy.Agent: startup recovery sweep completed (no expired sessions).");
        }

        return result;
    }

    /// <summary>
    /// Starts the background periodic sweep timer loop.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _runningLoop = Task.Run(() => RunPeriodicLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Runs both the startup sweep (if enabled) and the background periodic loop until cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var ct = linkedCts.Token;

        if (_options.RunStartupSweep)
        {
            await RunStartupSweepAsync(ct);
        }

        await RunPeriodicLoopAsync(ct);
    }

    private async Task RunPeriodicLoopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"BoundlessStudy.Agent: background recovery sweep active (interval: {_options.SweepInterval.TotalSeconds:F0}s).");

        using var timer = new PeriodicTimer(_options.SweepInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var result = await _executor.ExecuteSweepAsync(cancellationToken);
                    if (result is { SessionsAbandoned: > 0 })
                    {
                        Console.WriteLine(
                            $"BoundlessStudy.Agent: background recovery sweep abandoned {result.SessionsAbandoned} " +
                            $"expired session(s): [{string.Join(", ", result.AbandonedSessionIds)}].");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"BoundlessStudy.Agent: background sweep loop error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful cancellation
        }
        finally
        {
            Console.WriteLine("BoundlessStudy.Agent: background recovery sweep stopped.");
        }
    }

    public async Task StopAsync()
    {
        if (_disposed) return;
        _cts.Cancel();
        if (_runningLoop is not null)
        {
            try
            {
                await _runningLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on stop
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _cts.Dispose();
    }
}
