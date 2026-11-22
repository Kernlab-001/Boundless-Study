using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Starts a break (§6): only when no focus session is active and no other break is
/// active. Breaks award no XP and never change the streak.
/// </summary>
public sealed class StartBreakHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IBreakSessionRepository _breaks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public StartBreakHandler(
        IFocusSessionRepository sessions,
        IBreakSessionRepository breaks,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _sessions = sessions;
        _breaks = breaks;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
    }

    public async Task<EntityResult> HandleAsync(
        Guid localProfileId,
        int plannedSeconds,
        Guid? precedingFocusSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (await _sessions.FindActiveByProfileAsync(localProfileId, cancellationToken) is not null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ActiveFocusSessionExists,
                "A focus session is active. A break cannot be active while a focus session is active.");
        }

        if (await _breaks.FindActiveByProfileAsync(localProfileId, cancellationToken) is not null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ActiveBreakSessionExists, "A break is already active for this profile.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var session = BreakSession.Start(localProfileId, plannedSeconds, _clock.UtcNow, precedingFocusSessionId);
                    await _breaks.AddAsync(session, ct);
                    await _events.RecordAsync(SessionType.Break, session.Id, SessionEventType.BreakStarted, cancellationToken: ct);
                    return EntityResult.Ok(session.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Records a break checkpoint (monotonic elapsed, pauses during lock/sleep).</summary>
public sealed class ApplyBreakCheckpointHandler
{
    private readonly IBreakSessionRepository _breaks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public ApplyBreakCheckpointHandler(IBreakSessionRepository breaks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _breaks = breaks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid breakSessionId, int elapsedSeconds, CancellationToken cancellationToken = default)
    {
        var session = await _breaks.FindAsync(breakSessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The break session does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    session.ApplyCheckpoint(_clock.UtcNow, elapsedSeconds);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Completes a break or ends it early; completion awards nothing (§6).</summary>
public sealed class FinishBreakHandler
{
    private readonly IBreakSessionRepository _breaks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public FinishBreakHandler(
        IBreakSessionRepository breaks,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _breaks = breaks;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
    }

    public async Task<UseCaseResult> HandleAsync(
        Guid breakSessionId,
        int elapsedSeconds,
        bool endedEarly = false,
        BreakEndReason endReason = BreakEndReason.UserEnded,
        CancellationToken cancellationToken = default)
    {
        var session = await _breaks.FindAsync(breakSessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The break session does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    if (endedEarly)
                    {
                        session.EndEarly(_clock.UtcNow, elapsedSeconds, endReason);
                    }
                    else
                    {
                        session.Complete(_clock.UtcNow, elapsedSeconds);
                    }

                    await _events.RecordAsync(SessionType.Break, session.Id, SessionEventType.BreakEnded, cancellationToken: ct);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Records lock/sleep as an interrupted break.</summary>
public sealed class InterruptBreakHandler
{
    private readonly IBreakSessionRepository _breaks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public InterruptBreakHandler(IBreakSessionRepository breaks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _breaks = breaks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid breakSessionId, BreakEndReason reason, CancellationToken cancellationToken = default)
    {
        var session = await _breaks.FindAsync(breakSessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The break session does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    session.MarkInterrupted(_clock.UtcNow, reason);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}
