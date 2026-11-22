using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Domain.ValueObjects;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Starts a focus session (§5.4, §6). Enforces: one active focus per profile, no
/// active break, and one active link per task. A pending RecoverableInterrupted
/// session blocks a new start until it is resumed or discarded; once it is at least
/// 24 hours old it is automatically abandoned and the new session may proceed
/// (A6, approved 2026-11-19). The application-rule version is app-owned, never
/// caller-supplied (A2, approved 2026-11-19). Snapshots are captured at start and
/// never rewritten.
/// </summary>
public sealed class StartFocusSessionHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusProfileRepository _focusProfiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly IBreakSessionRepository _breaks;
    private readonly IStudyTaskRepository _tasks;
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;
    private readonly AbandonFocusSessionHandler _abandon;

    public StartFocusSessionHandler(
        ILocalProfileRepository profiles,
        IFocusProfileRepository focusProfiles,
        IFocusSessionRepository sessions,
        IBreakSessionRepository breaks,
        IStudyTaskRepository tasks,
        ISubjectRepository subjects,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events,
        AbandonFocusSessionHandler abandon)
    {
        _profiles = profiles;
        _focusProfiles = focusProfiles;
        _sessions = sessions;
        _breaks = breaks;
        _tasks = tasks;
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
        _abandon = abandon;
    }

    public async Task<EntityResult> HandleAsync(
        Guid localProfileId,
        string profileCode,
        int plannedFocusSeconds,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        var focusProfile = await _focusProfiles.FindAsync(profileCode, cancellationToken);
        if (focusProfile is null || !focusProfile.IsEnabled)
        {
            return EntityResult.Fail(UseCaseErrorCodes.FocusProfileNotFound, $"The focus profile '{profileCode}' does not exist or is disabled.");
        }

        var now = _clock.UtcNow;
        var pendingRecovery = await _sessions.FindActiveByProfileAsync(localProfileId, cancellationToken);
        if (pendingRecovery is { State: SessionState.RecoverableInterrupted }
            && !pendingRecovery.IsPendingInterruptionExpired(now))
        {
            return EntityResult.Fail(UseCaseErrorCodes.RecoverableSessionPending,
                "An interrupted session is awaiting recovery. Resume or discard it before starting a new session.");
        }

        if (pendingRecovery is not null && pendingRecovery.State is not SessionState.RecoverableInterrupted)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ActiveFocusSessionExists, "A focus session is already active for this profile.");
        }

        if (await _breaks.FindActiveByProfileAsync(localProfileId, cancellationToken) is not null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ActiveBreakSessionExists,
                "A break is active. It cannot overlap a focus session.");
        }

        string? taskTitleSnapshot = null;
        Guid? subjectId = null;
        string? subjectNameSnapshot = null;

        if (taskId is { } tid)
        {
            var task = await _tasks.FindAsync(tid, cancellationToken);
            if (task is null)
            {
                return EntityResult.Fail(UseCaseErrorCodes.TaskNotFound, "The linked task does not exist.");
            }

            if (task.IsDeleted)
            {
                return EntityResult.Fail(UseCaseErrorCodes.TaskDeleted, "A deleted task cannot be linked to a new session.");
            }

            var activeTaskSession = await _sessions.FindActiveByTaskAsync(tid, cancellationToken);
            if (activeTaskSession is not null && activeTaskSession.Id != pendingRecovery?.Id)
            {
                return EntityResult.Fail(UseCaseErrorCodes.ActiveFocusSessionExists, "This task already has an active session link.");
            }

            taskTitleSnapshot = task.Title;
            if (task.SubjectId is { } sid)
            {
                var subject = await _subjects.FindAsync(sid, cancellationToken);
                if (subject is not null)
                {
                    subjectId = subject.Id;
                    subjectNameSnapshot = subject.Name;
                }
            }
        }

        if (pendingRecovery is { State: SessionState.RecoverableInterrupted } expiredSession)
        {
            // The expiry transition and its terminal reward are committed before the
            // new session is created. A repeated start is idempotent: the old session
            // is already terminal and its ledger entry is keyed by session identity.
            var abandonment = await _abandon.HandleAsync(expiredSession.Id, FocusEndReason.Unknown, cancellationToken);
            if (!abandonment.Success)
            {
                var error = abandonment.Error
                    ?? new UseCaseError(UseCaseErrorCodes.DomainRuleViolation, "The expired session could not be abandoned.");
                return EntityResult.Fail(error.Code, error.Message);
            }
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var session = FocusSession.Start(
                        localProfileId,
                        focusProfile.Code,
                        plannedFocusSeconds,
                        now,
                        focusProfile.EconomyRuleVersion,
                        taskId,
                        subjectId,
                        taskTitleSnapshot,
                        subjectNameSnapshot);
                    await _sessions.AddAsync(session, ct);
                    await _events.RecordAsync(SessionType.Focus, session.Id, SessionEventType.Start, cancellationToken: ct);
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

/// <summary>Records a checkpoint (§3.6): counters, elapsed, active, gaps.</summary>
public sealed class ApplyFocusCheckpointHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public ApplyFocusCheckpointHandler(IFocusSessionRepository sessions, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        int violationCount,
        int exceptionCount,
        int recoveryCount,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    session.ApplyCheckpoint(
                        _clock.UtcNow,
                        Math.Max(session.ElapsedSeconds, elapsedSeconds),
                        Math.Max(session.ActiveStudySeconds, activeStudySeconds),
                        Math.Max(session.IdleSeconds, idleSeconds),
                        Math.Max(session.ProtectionGapSeconds, protectionGapSeconds),
                        Math.Max(session.ViolationCount, violationCount),
                        Math.Max(session.ExceptionCount, exceptionCount),
                        Math.Max(session.RecoveryCount, recoveryCount));
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

/// <summary>Blocked app is foreground with the overlay up (Active → Blocked), and back (Blocked → Active).</summary>
public sealed class FocusBlockStateHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public FocusBlockStateHandler(
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

    public async Task<UseCaseResult> HandleAsync(Guid sessionId, bool blocked, CancellationToken cancellationToken = default)
    {
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
                    if (blocked)
                    {
                        session.Block(_clock.UtcNow);
                    }
                    else
                    {
                        session.Unblock(_clock.UtcNow);
                    }

                    await _events.RecordAsync(
                        SessionType.Focus,
                        session.Id,
                        blocked ? SessionEventType.Blocked : SessionEventType.Unblocked,
                        cancellationToken: ct);
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

/// <summary>Lock/sleep pause and resume (§5.3). Downtime is not credited.</summary>
public sealed class FocusSystemPauseHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public FocusSystemPauseHandler(
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

    public async Task<UseCaseResult> HandleAsync(Guid sessionId, bool paused, CancellationToken cancellationToken = default)
    {
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
                    if (paused)
                    {
                        session.SystemPause(_clock.UtcNow);
                    }
                    else
                    {
                        session.SystemResume(_clock.UtcNow);
                    }

                    await _events.RecordAsync(
                        SessionType.Focus,
                        session.Id,
                        paused ? SessionEventType.SystemPaused : SessionEventType.SystemResumed,
                        cancellationToken: ct);
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

/// <summary>
/// Records an interruption (agent crash / shutdown / power loss → RecoverableInterrupted).
/// Not a terminal reward state; no ledger entry is created (§8.3).
/// </summary>
public sealed class MarkFocusInterruptedHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public MarkFocusInterruptedHandler(
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

    public async Task<UseCaseResult> HandleAsync(
        Guid sessionId,
        FocusEndReason reason,
        CancellationToken cancellationToken = default,
        DateTime? occurredAtUtc = null)
    {
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
                    var atUtc = occurredAtUtc.HasValue
                        ? DateTime.SpecifyKind(occurredAtUtc.Value, DateTimeKind.Utc)
                        : _clock.UtcNow;
                    session.MarkRecoverableInterrupted(atUtc, reason);
                    await _events.RecordAtAsync(atUtc, SessionType.Focus, session.Id, SessionEventType.Interrupted, cancellationToken: ct);
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

/// <summary>
/// Recovers a RecoverableInterrupted session (§5.2). When the checkpoint already
/// met the planned duration the session is finalized as Completed — through the
/// full idempotent reward transaction. Otherwise it returns to Active; downtime is
/// not credited and restrictions are reapplied by the agent.
/// </summary>
public sealed class RecoverFocusSessionHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalProfileRepository _profiles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RewardService _rewards;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public RecoverFocusSessionHandler(
        IFocusSessionRepository sessions,
        ILocalProfileRepository profiles,
        IUnitOfWork unitOfWork,
        RewardService rewards,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _sessions = sessions;
        _profiles = profiles;
        _unitOfWork = unitOfWork;
        _rewards = rewards;
        _clock = clock;
        _events = events;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        var now = _clock.UtcNow;

        if (session.State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned)
        {
            // Repeated Resume/Discard after a terminal result is idempotent.
            return await ReturnExistingOutcomeAsync(session, cancellationToken);
        }

        try
        {
            if (session.IsPendingInterruptionExpired(now))
            {
                // The 24-hour policy wins over an attempted resume. The session is
                // abandoned once and the normal terminal reward path runs; the
                // boundary is inclusive at exactly 24:00:00 (A6).
                session.Abandon(now, FocusEndReason.Unknown);
                return await AwardTerminalSessionAsync(session, now, cancellationToken: cancellationToken);
            }

            if (session.LastCheckpointElapsedSeconds is { } checkpointElapsed && checkpointElapsed >= session.PlannedFocusSeconds)
            {
                session.CompleteFromCheckpoint(now);
                return await AwardTerminalSessionAsync(session, now, cancellationToken: cancellationToken);
            }

            session.Recover(now);
            await _events.RecordAsync(SessionType.Focus, session.Id, SessionEventType.Recovered, cancellationToken: cancellationToken);
            return new SessionCompletionOutcome(true, null, true, 0, await _rewards.TotalXpAsync(session.LocalProfileId, cancellationToken), 0, 0, false, Array.Empty<string>());
        }
        catch (DomainException ex)
        {
            return SessionCompletionOutcome.Failed(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Idempotent retry path (§8.10, TV-13/TV-14): the session already reached a
    /// terminal reward state. Returns the existing entry unchanged when present;
    /// a cap-zero qualified completion has no entry and returns success-with-no-change.
    /// </summary>
    internal async Task<SessionCompletionOutcome> ReturnExistingOutcomeAsync(
        FocusSession session,
        CancellationToken cancellationToken)
    {
        var sourceType = session.State == SessionState.Completed
            ? LedgerSourceType.SessionCompletion
            : LedgerSourceType.SessionPartial;
        var key = IdempotencyKey.For(sourceType, session.Id.ToString(), CurrencyCode.Xp).Value;
        var entry = await _rewards.FindEntryAsync(key, cancellationToken);
        var totalXp = await _rewards.TotalXpAsync(session.LocalProfileId, cancellationToken);
        var level = EconomyRules.LevelForTotalXp(totalXp);
        var qualified = EconomyRules.IsStreakQualified(session.State, session.ActiveStudySeconds);

        return new SessionCompletionOutcome(
            true, null, false, entry?.Amount ?? 0, totalXp, level, level, qualified, Array.Empty<string>());
    }

    internal async Task<SessionCompletionOutcome> AwardTerminalSessionAsync(
        FocusSession session,
        DateTime now,
        string terminalEventPayload = "",
        CancellationToken cancellationToken = default)
    {
        // The terminal transition and its event are recorded only after the
        // transaction starts. A repository SaveChanges during event insertion must
        // never make a terminal state durable before its reward transaction commits.
        return await CommandBoundary.ExecuteAsync(
            _unitOfWork,
            async ct =>
            {
                var profile = await _profiles.FindAsync(session.LocalProfileId, ct);
                if (profile is null)
                {
                    return SessionCompletionOutcome.Failed(
                        UseCaseErrorCodes.ProfileNotFound,
                        "The local profile does not exist.");
                }

                var terminalEvent = session.State switch
                {
                    SessionState.Completed => SessionEventType.Completed,
                    SessionState.EndedEarly => SessionEventType.EndedEarly,
                    SessionState.Abandoned => SessionEventType.Abandoned,
                    _ => throw new DomainException("SESSION_NOT_REWARD_TERMINAL",
                        $"State {session.State} is not a terminal reward state."),
                };
                await _events.RecordAsync(SessionType.Focus, session.Id, terminalEvent, terminalEventPayload, ct);

                var totalXpBefore = await _rewards.TotalXpAsync(session.LocalProfileId, ct);
                var levelBefore = EconomyRules.LevelForTotalXp(totalXpBefore);

                var award = await _rewards.ComputeSessionAwardAsync(session, ct);
                var streakQualified = EconomyRules.IsStreakQualified(session.State, session.ActiveStudySeconds);

                var (entry, alreadyExisted) = await _rewards.AwardAsync(
                    session.LocalProfileId,
                    CurrencyCode.Xp,
                    award,
                    session.State == SessionState.Completed ? LedgerSourceType.SessionCompletion : LedgerSourceType.SessionPartial,
                    session.Id.ToString(),
                    notes: session.State == SessionState.Completed ? "Focus session completion" : "Focus session partial award",
                    ct);

                if (entry is not null)
                {
                    await _events.RecordAsync(SessionType.Focus, session.Id, SessionEventType.RewardCreated, cancellationToken: ct);
                }

                var achievements = await _rewards.EvaluateAndUnlockAchievementsAsync(session.LocalProfileId, ct);
                foreach (var code in achievements)
                {
                    await _events.RecordAsync(SessionType.Focus, session.Id, SessionEventType.AchievementUnlocked, code, ct);
                }

                var totalXpAfter = await _rewards.TotalXpAsync(session.LocalProfileId, ct);
                var levelAfter = EconomyRules.LevelForTotalXp(totalXpAfter);
                profile.SetCachedRewardTotals(totalXpAfter, levelAfter, now);

                if (alreadyExisted && entry is not null)
                {
                    // Crash-after-commit retry (TV-13 / TV-14): the entry exists; return it unchanged.
                    return new SessionCompletionOutcome(true, null, false, entry.Amount, totalXpAfter, levelAfter, levelAfter, streakQualified, achievements);
                }

                return new SessionCompletionOutcome(true, null, true, award, totalXpAfter, levelBefore, levelAfter, streakQualified, achievements);
            },
            cancellationToken);
    }
}

/// <summary>
/// Normal completion (Active → Completed, elapsed ≥ planned) with the full reward
/// transaction (§8.3–8.6, §8.11): terminal state, cap-reduced session XP, streak
/// qualification, achievement unlocks, and cached totals in one transaction.
/// </summary>
public sealed class CompleteFocusSessionHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalClock _clock;
    private readonly RecoverFocusSessionHandler _recoveryAward;

    public CompleteFocusSessionHandler(
        IFocusSessionRepository sessions,
        ILocalClock clock,
        RecoverFocusSessionHandler recoveryAward)
    {
        _sessions = sessions;
        _clock = clock;
        _recoveryAward = recoveryAward;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (session.State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned)
        {
            // Idempotent retry after an uncertain result (TV-13 / TV-14).
            return await _recoveryAward.ReturnExistingOutcomeAsync(session, cancellationToken);
        }

        try
        {
            session.Complete(
                _clock.UtcNow,
                Math.Max(session.ElapsedSeconds, elapsedSeconds),
                Math.Max(session.ActiveStudySeconds, activeStudySeconds),
                Math.Max(session.IdleSeconds, idleSeconds),
                Math.Max(session.ProtectionGapSeconds, protectionGapSeconds));
        }
        catch (DomainException ex)
        {
            return SessionCompletionOutcome.Failed(ex.Code, ex.Message);
        }

        return await _recoveryAward.AwardTerminalSessionAsync(session, _clock.UtcNow, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// User ends the session early (Active → EndedEarly) with the recorded reason and
/// remaining time (§4.3). Partial XP may apply (0.5 multiplier); never streak-qualified.
/// </summary>
public sealed class EndFocusEarlyHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalClock _clock;
    private readonly RecoverFocusSessionHandler _recoveryAward;

    public EndFocusEarlyHandler(
        IFocusSessionRepository sessions,
        ILocalClock clock,
        RecoverFocusSessionHandler recoveryAward)
    {
        _sessions = sessions;
        _clock = clock;
        _recoveryAward = recoveryAward;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        FocusEndReason endReason,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (session.State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned)
        {
            return await _recoveryAward.ReturnExistingOutcomeAsync(session, cancellationToken);
        }

        try
        {
            session.EndEarly(
                _clock.UtcNow,
                Math.Max(session.ElapsedSeconds, elapsedSeconds),
                Math.Max(session.ActiveStudySeconds, activeStudySeconds),
                Math.Max(session.IdleSeconds, idleSeconds),
                Math.Max(session.ProtectionGapSeconds, protectionGapSeconds),
                endReason);
        }
        catch (DomainException ex)
        {
            return SessionCompletionOutcome.Failed(ex.Code, ex.Message);
        }

        var payload = $"{{\"reason\":\"{endReason}\",\"remainingSeconds\":{Math.Max(0, session.PlannedFocusSeconds - elapsedSeconds)}}}";
        return await _recoveryAward.AwardTerminalSessionAsync(session, _clock.UtcNow, payload, cancellationToken);
    }
}

/// <summary>
/// Abandons a RecoverableInterrupted session (expired / declined / unrecoverable).
/// Abandoned is a terminal reward state: the 0.5 multiplier applies once (§8.3).
/// </summary>
public sealed class AbandonFocusSessionHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalClock _clock;
    private readonly RecoverFocusSessionHandler _recoveryAward;

    public AbandonFocusSessionHandler(
        IFocusSessionRepository sessions,
        ILocalClock clock,
        RecoverFocusSessionHandler recoveryAward)
    {
        _sessions = sessions;
        _clock = clock;
        _recoveryAward = recoveryAward;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(
        Guid sessionId,
        FocusEndReason abandonmentReason,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (session.State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned)
        {
            return await _recoveryAward.ReturnExistingOutcomeAsync(session, cancellationToken);
        }

        try
        {
            session.Abandon(_clock.UtcNow, abandonmentReason);
        }
        catch (DomainException ex)
        {
            return SessionCompletionOutcome.Failed(ex.Code, ex.Message);
        }

        return await _recoveryAward.AwardTerminalSessionAsync(session, _clock.UtcNow, cancellationToken: cancellationToken);
    }
}

/// <summary>Updates protection/verification status from the agent's checks (§7.4).</summary>
public sealed class SetFocusProtectionHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public SetFocusProtectionHandler(IFocusSessionRepository sessions, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(
        Guid sessionId,
        ProtectionStatus protectionStatus,
        VerificationStatus verificationStatus,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        return await CommandBoundary.ExecuteAsync(
            _unitOfWork,
            () =>
            {
                session.SetProtection(protectionStatus, verificationStatus, _clock.UtcNow);
                return UseCaseResult.Ok();
            },
            cancellationToken);
    }
}
