using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Infrastructure.Persistence;
using BoundlessStudy.Infrastructure.Services;

namespace BoundlessStudy.Agent;

/// <summary>Connects Windows lock, power, idle, and shutdown signals to Agent-owned session state.</summary>
public sealed class SystemStateAgentCoordinator : IDisposable
{
    private readonly SqliteDataStore _store;
    private readonly ILocalClock _clock;
    private readonly WindowsSystemStateTracker _tracker;
    private readonly SystemPauseSignalState _pauseSignals = new();
    private readonly object _queueLock = new();
    private Task _transitionTail = Task.CompletedTask;
    private bool _started;

    public SystemStateAgentCoordinator(SqliteDataStore store, ILocalClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tracker = new WindowsSystemStateTracker(clock);
        _tracker.WorkstationLockChanged += OnLockChanged;
        _tracker.PowerSuspendChanged += OnPowerChanged;
        _tracker.IdleStateChanged += OnIdleChanged;
        _tracker.SystemEnding += OnSystemEnding;
    }

    public void Start()
    {
        if (_started || !OperatingSystem.IsWindows()) return;
        _started = true;
        _tracker.Start();
    }

    private void OnLockChanged(object? sender, WorkstationLockEventArgs e) =>
        EnqueueTransitionAsync(async () =>
        {
            var paused = _pauseSignals.SetLocked(e.IsLocked, e.OccurredAtUtc);
            if (paused.HasValue) await ApplySystemPauseAsync(paused.Value, e.OccurredAtUtc);
        });

    private void OnPowerChanged(object? sender, PowerSuspendEventArgs e) =>
        EnqueueTransitionAsync(async () =>
        {
            var paused = _pauseSignals.SetSuspended(e.IsSuspended, e.OccurredAtUtc);
            if (paused.HasValue) await ApplySystemPauseAsync(paused.Value, e.OccurredAtUtc);
        });

    private void OnIdleChanged(object? sender, UserIdleEventArgs e) =>
        EnqueueTransitionAsync(async () => await ProcessTransitionAsync(async (sessionId, repos, ct) =>
        {
            var state = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock,
                new SessionEventRecorder(repos.SessionEvents, _clock));
            await state.HandleUserIdleAsync(sessionId, e.IsIdle, e.IdleDuration, ct, e.OccurredAtUtc);
        }, CancellationToken.None));

    private void OnSystemEnding(object? sender, SystemEndingEventArgs e)
    {
        // SessionEnding is a final checkpoint boundary; synchronously finish the small local
        // database transaction so Windows logoff/shutdown does not lose the recoverable state.
        try
        {
            EnqueueTransitionAsync(() => ProcessTransitionAsync(async (sessionId, repos, ct) =>
            {
                var recorder = new SessionEventRecorder(repos.SessionEvents, _clock);
                var interrupted = new MarkFocusInterruptedHandler(repos.FocusSessions, repos.UnitOfWork, _clock, recorder);
                await interrupted.HandleAsync(sessionId, FocusEndReason.Shutdown, ct, e.OccurredAtUtc);
            }, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BoundlessStudy.Agent: could not persist session ending state ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Applies the app overlay's enforcement state to the existing SystemPaused state.
    /// The focus session is revalidated before the cause is updated; the session resumes
    /// only after app enforcement, workstation lock, and sleep causes have all cleared.
    /// </summary>
    public Task SetApplicationProtectionFailureAsync(
        Guid sessionId,
        bool failed,
        DateTime? occurredAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var atUtc = NormalizeUtc(occurredAtUtc ?? _clock.UtcNow);
        return EnqueueTransitionAsync(async () =>
        {
            await using var repos = _store.CreateRepositories();
            var profile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
            if (profile is null) return;
            var session = await repos.FocusSessions.FindAsync(sessionId, cancellationToken);
            if (session is null || session.LocalProfileId != profile.Id || !session.IsActive) return;

            var paused = _pauseSignals.SetApplicationProtectionFailure(sessionId, failed, atUtc);
            if (paused.HasValue)
            {
                var state = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock,
                    new SessionEventRecorder(repos.SessionEvents, _clock));
                await state.HandleWorkstationLockAsync(sessionId, paused.Value, cancellationToken, atUtc);
            }
        });
    }

    private Task ApplySystemPauseAsync(bool isPaused, DateTime occurredAtUtc)
    {
        return ProcessTransitionAsync(async (sessionId, repos, ct) =>
        {
            var state = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock,
                new SessionEventRecorder(repos.SessionEvents, _clock));
            await state.HandleWorkstationLockAsync(sessionId, isPaused, ct, occurredAtUtc);
        }, CancellationToken.None);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Serializes notifications in arrival order. A task-chain is used instead of starting
    /// independent async handlers behind a semaphore, whose acquisition order is unspecified.
    /// Each queued operation catches its own failure so later transitions still run.
    /// </summary>
    private Task EnqueueTransitionAsync(Func<Task> transition)
    {
        lock (_queueLock)
        {
            _transitionTail = _transitionTail.ContinueWith(async _ =>
            {
                try { await transition(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"BoundlessStudy.Agent: system-state transition failed ({ex.GetType().Name}).");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _transitionTail;
        }
    }

    private async Task ProcessTransitionAsync(
        Func<Guid, SqliteRepositorySet, CancellationToken, Task> transition,
        CancellationToken cancellationToken)
    {
        await using var repos = _store.CreateRepositories();
        var profile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
        if (profile is null) return;
        var session = await repos.FocusSessions.FindActiveByProfileAsync(profile.Id, cancellationToken);
        if (session is null || !session.IsActive) return;
        await transition(session.Id, repos, cancellationToken);
    }

    public void Dispose()
    {
        _tracker.Stop();
        _tracker.WorkstationLockChanged -= OnLockChanged;
        _tracker.PowerSuspendChanged -= OnPowerChanged;
        _tracker.IdleStateChanged -= OnIdleChanged;
        _tracker.SystemEnding -= OnSystemEnding;
        _tracker.Dispose();
        Task tail;
        lock (_queueLock) tail = _transitionTail;
        try { tail.GetAwaiter().GetResult(); }
        catch { /* queue handlers report their own failures */ }
    }
}
