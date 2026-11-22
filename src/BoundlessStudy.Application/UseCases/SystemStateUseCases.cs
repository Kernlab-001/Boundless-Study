using System.Text.Json;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Use cases for system state transitions (lock, sleep, user idle) and protection gap tracking
/// (conceptual-data-model.md §4.7, domain-business-rules.md §5.1, §5.3, §7.2, §7.4).
/// </summary>
public sealed class SystemStateUseCases
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public SystemStateUseCases(
        IFocusSessionRepository sessions,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
    }

    /// <summary>
    /// Handles workstation lock or unlock transitions (§5.3).
    /// Lock: Active -> SystemPaused, records SystemPaused event.
    /// Unlock: SystemPaused -> Active, records SystemResumed event.
    /// Operations are idempotent; downtime is not credited.
    /// </summary>
    public async Task<UseCaseResult> HandleWorkstationLockAsync(
        Guid sessionId,
        bool isLocked,
        CancellationToken cancellationToken = default,
        DateTime? occurredAtUtc = null)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (isLocked)
        {
            if (session.State == SessionState.SystemPaused)
            {
                return UseCaseResult.Ok(changed: false);
            }

            if (session.State != SessionState.Active)
            {
                return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotActive, $"Cannot lock session in state '{session.State}'.");
            }
        }
        else
        {
            if (session.State == SessionState.Active)
            {
                return UseCaseResult.Ok(changed: false);
            }

            if (session.State != SessionState.SystemPaused)
            {
                return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotActive, $"Cannot unlock session in state '{session.State}'.");
            }
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var now = NormalizeOccurredAtUtc(occurredAtUtc);
                    if (isLocked)
                    {
                        session.SystemPause(now);
                    }
                    else
                    {
                        session.SystemResume(now);
                    }

                    await _events.RecordAtAsync(
                        now,
                        SessionType.Focus,
                        session.Id,
                        isLocked ? SessionEventType.SystemPaused : SessionEventType.SystemResumed,
                        cancellationToken: ct);

                    return UseCaseResult.Ok(changed: true);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Handles system power suspend (sleep) or resume transitions (§5.3).
    /// Sleep: Active -> SystemPaused, records SystemPaused event.
    /// Resume: SystemPaused -> Active, records SystemResumed event.
    /// </summary>
    public async Task<UseCaseResult> HandleSystemSleepAsync(
        Guid sessionId,
        bool isSuspended,
        CancellationToken cancellationToken = default,
        DateTime? occurredAtUtc = null)
    {
        // Power mode transitions follow identical domain rules to lock transitions
        return await HandleWorkstationLockAsync(sessionId, isSuspended, cancellationToken, occurredAtUtc);
    }

    /// <summary>
    /// Handles user idle state transitions (domain-business-rules.md §5).
    /// Records IdleStarted and IdleEnded events with duration payload.
    /// Accumulates excess idle time (beyond the 5-minute fixed threshold) into session metrics.
    /// </summary>
    public async Task<UseCaseResult> HandleUserIdleAsync(
        Guid sessionId,
        bool isIdle,
        TimeSpan? idleDuration = null,
        CancellationToken cancellationToken = default,
        DateTime? occurredAtUtc = null)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (idleDuration is { } requestedDuration && requestedDuration < TimeSpan.Zero)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.ValidationFailed, "Idle duration must be non-negative.");
        }

        if (!session.IsActive && session.State != SessionState.Blocked && session.State != SessionState.SystemPaused)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotActive, "Idle events can only be processed on non-terminal sessions.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    // The tracker can deliver duplicate notifications (for example, a
                    // queued timer callback racing an input event). Treat the last
                    // persisted idle transition as the state so retries do not append
                    // duplicate events or count the same idle interval twice.
                    var idleEvents = await _events.ListAsync(SessionType.Focus, session.Id, ct);
                    var lastIdleEvent = idleEvents.LastOrDefault(e =>
                        e.EventType is SessionEventType.IdleStarted or SessionEventType.IdleEnded);
                    var wasIdle = lastIdleEvent?.EventType == SessionEventType.IdleStarted;
                    if (wasIdle == isIdle)
                    {
                        return UseCaseResult.Ok(changed: false);
                    }

                    var now = NormalizeOccurredAtUtc(occurredAtUtc);
                    var durationSec = idleDuration.HasValue
                        ? (int)Math.Min(idleDuration.Value.TotalSeconds, int.MaxValue)
                        : 0;
                    var payload = JsonSerializer.Serialize(new
                    {
                        isIdle,
                        idleDurationSeconds = durationSec,
                        occurredAtUtc = now.ToString("o")
                    });

                    // If idle ended and duration exceeded the 5-minute (300s) threshold, accumulate into session
                    if (!isIdle && durationSec > 300)
                    {
                        var excessIdle = durationSec - 300;
                        session.ApplyCheckpoint(
                            now,
                            session.ElapsedSeconds,
                            session.ActiveStudySeconds,
                            session.IdleSeconds + excessIdle,
                            session.ProtectionGapSeconds,
                            session.ViolationCount,
                            session.ExceptionCount,
                            session.RecoveryCount);
                    }

                    await _events.RecordAtAsync(
                        now,
                        SessionType.Focus,
                        session.Id,
                        isIdle ? SessionEventType.IdleStarted : SessionEventType.IdleEnded,
                        payload,
                        ct);

                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Handles protection status changes and records ProtectionDegraded or ProtectionRestored events (§7.2, §7.4).
    /// </summary>
    public async Task<UseCaseResult> HandleProtectionStatusChangedAsync(
        Guid sessionId,
        ProtectionStatus newStatus,
        VerificationStatus verificationStatus,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        var oldStatus = session.ProtectionStatus;
        if (oldStatus == newStatus && session.VerificationStatus == verificationStatus)
        {
            return UseCaseResult.Ok(changed: false);
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var now = _clock.UtcNow;
                    session.SetProtection(newStatus, verificationStatus, now);

                    if (oldStatus == ProtectionStatus.Protected && newStatus != ProtectionStatus.Protected)
                    {
                        // Protection has degraded
                        var payload = JsonSerializer.Serialize(new
                        {
                            previousStatus = oldStatus.ToString(),
                            newStatus = newStatus.ToString(),
                            verificationStatus = verificationStatus.ToString(),
                            occurredAtUtc = now.ToString("o")
                        });

                        await _events.RecordAtAsync(
                            now,
                            SessionType.Focus,
                            session.Id,
                            SessionEventType.ProtectionDegraded,
                            payload,
                            ct);
                    }
                    else if (oldStatus != ProtectionStatus.Protected && newStatus == ProtectionStatus.Protected)
                    {
                        // Protection has been restored
                        var payload = JsonSerializer.Serialize(new
                        {
                            previousStatus = oldStatus.ToString(),
                            newStatus = newStatus.ToString(),
                            verificationStatus = verificationStatus.ToString(),
                            occurredAtUtc = now.ToString("o")
                        });

                        await _events.RecordAtAsync(
                            now,
                            SessionType.Focus,
                            session.Id,
                            SessionEventType.ProtectionRestored,
                            payload,
                            ct);
                    }

                    return UseCaseResult.Ok(changed: true);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Records an accurately computed protection gap increment in seconds (domain-business-rules.md §7.2 line 247).
    /// </summary>
    public async Task<UseCaseResult> RecordProtectionGapAsync(
        Guid sessionId,
        int gapSeconds,
        CancellationToken cancellationToken = default)
    {
        if (gapSeconds < 0)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.ValidationFailed, "Protection gap seconds must be non-negative.");
        }

        if (gapSeconds == 0)
        {
            return UseCaseResult.Ok(changed: false);
        }

        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var now = _clock.UtcNow;
                    var newGapSeconds = session.ProtectionGapSeconds + gapSeconds;

                    session.ApplyCheckpoint(
                        now,
                        session.ElapsedSeconds,
                        session.ActiveStudySeconds,
                        session.IdleSeconds,
                        newGapSeconds,
                        session.ViolationCount,
                        session.ExceptionCount,
                        session.RecoveryCount);

                    var payload = JsonSerializer.Serialize(new
                    {
                        gapDeltaSeconds = gapSeconds,
                        totalProtectionGapSeconds = newGapSeconds,
                        occurredAtUtc = now.ToString("o")
                    });

                    await _events.RecordAtAsync(
                        now,
                        SessionType.Focus,
                        session.Id,
                        SessionEventType.ProtectionDegraded,
                        payload,
                        ct);

                    return UseCaseResult.Ok(changed: true);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }

    private DateTime NormalizeOccurredAtUtc(DateTime? occurredAtUtc)
    {
        if (!occurredAtUtc.HasValue)
        {
            return _clock.UtcNow;
        }

        var value = occurredAtUtc.Value;
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}

/// <summary>Dedicated handler for workstation lock/unlock transitions.</summary>
public sealed class FocusSystemLockHandler
{
    private readonly SystemStateUseCases _useCases;

    public FocusSystemLockHandler(SystemStateUseCases useCases) => _useCases = useCases;

    public Task<UseCaseResult> HandleAsync(Guid sessionId, bool isLocked, CancellationToken ct = default, DateTime? occurredAtUtc = null) =>
        _useCases.HandleWorkstationLockAsync(sessionId, isLocked, ct, occurredAtUtc);
}

/// <summary>Dedicated handler for system sleep/resume transitions.</summary>
public sealed class FocusSystemSleepHandler
{
    private readonly SystemStateUseCases _useCases;

    public FocusSystemSleepHandler(SystemStateUseCases useCases) => _useCases = useCases;

    public Task<UseCaseResult> HandleAsync(Guid sessionId, bool isSuspended, CancellationToken ct = default, DateTime? occurredAtUtc = null) =>
        _useCases.HandleSystemSleepAsync(sessionId, isSuspended, ct, occurredAtUtc);
}

/// <summary>Dedicated handler for user idle start and end events.</summary>
public sealed class FocusUserIdleHandler
{
    private readonly SystemStateUseCases _useCases;

    public FocusUserIdleHandler(SystemStateUseCases useCases) => _useCases = useCases;

    public Task<UseCaseResult> HandleAsync(Guid sessionId, bool isIdle, TimeSpan? idleDuration = null, CancellationToken ct = default, DateTime? occurredAtUtc = null) =>
        _useCases.HandleUserIdleAsync(sessionId, isIdle, idleDuration, ct, occurredAtUtc);
}

/// <summary>Dedicated handler for computing and recording protection gap seconds.</summary>
public sealed class FocusProtectionGapHandler
{
    private readonly SystemStateUseCases _useCases;

    public FocusProtectionGapHandler(SystemStateUseCases useCases) => _useCases = useCases;

    public Task<UseCaseResult> HandleGapAsync(Guid sessionId, int gapSeconds, CancellationToken ct = default) =>
        _useCases.RecordProtectionGapAsync(sessionId, gapSeconds, ct);

    public Task<UseCaseResult> HandleStatusChangeAsync(Guid sessionId, ProtectionStatus newStatus, VerificationStatus verificationStatus, CancellationToken ct = default) =>
        _useCases.HandleProtectionStatusChangedAsync(sessionId, newStatus, verificationStatus, ct);
}
