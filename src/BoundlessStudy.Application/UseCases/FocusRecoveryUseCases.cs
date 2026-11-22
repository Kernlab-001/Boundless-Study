using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Read-only description of the one unresolved interrupted session for a profile.
/// <see cref="IsExpired"/> is advisory: the expiry is enforced when the user tries to
/// resume or start a new session (A6, approved 2026-11-19). An expired session is
/// abandoned automatically by those commands, not by this query.
/// </summary>
public sealed record PendingFocusSessionInfo(
    Guid SessionId,
    string ProfileCode,
    string? TaskTitleSnapshot,
    DateTime StartedAtUtc,
    DateTime? InterruptedAtUtc,
    DateTime EffectiveInterruptionAnchorUtc,
    DateTime ExpiresAtUtc,
    TimeSpan Age,
    bool IsExpired,
    int PlannedFocusSeconds,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    TimeSpan RemainingTime = default)
{
    private readonly TimeSpan? _remainingTime = RemainingTime == default ? null : RemainingTime;

    /// <summary>
    /// Remaining recovery window before automatic abandonment at 24 hours.
    /// Clamped to zero when expired or when past expiration.
    /// </summary>
    public TimeSpan RemainingTime
    {
        get => _remainingTime ?? (IsExpired ? TimeSpan.Zero : (ExpiresAtUtc > EffectiveInterruptionAnchorUtc + Age ? ExpiresAtUtc - (EffectiveInterruptionAnchorUtc + Age) : TimeSpan.Zero));
        init => _remainingTime = value;
    }

    /// <summary>Remaining recovery time alias.</summary>
    public TimeSpan RemainingRecoveryTime => RemainingTime;
}

/// <summary>
/// Returns the pending RecoverableInterrupted session for the Resume/Discard surface,
/// or null when there is none. The query never mutates state and never opens a write
/// transaction (domain-business-rules.md §5.4; A6).
/// </summary>
public sealed class GetPendingFocusSessionHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalClock _clock;

    public GetPendingFocusSessionHandler(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        ILocalClock clock)
    {
        _profiles = profiles;
        _sessions = sessions;
        _clock = clock;
    }

    public async Task<PendingFocusSessionInfo?> HandleAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return null;
        }

        var session = await _sessions.FindActiveByProfileAsync(localProfileId, cancellationToken);
        if (session is not { State: SessionState.RecoverableInterrupted })
        {
            return null;
        }

        var now = _clock.UtcNow;
        var anchor = session.EffectiveInterruptionAnchorUtc;
        var expiresAtUtc = anchor + SessionRecoveryPolicy.PendingInterruptionLifetime;
        var age = session.PendingInterruptionAge(now);
        var isExpired = session.IsPendingInterruptionExpired(now);
        var remainingTime = isExpired
            ? TimeSpan.Zero
            : (SessionRecoveryPolicy.PendingInterruptionLifetime > age
                ? SessionRecoveryPolicy.PendingInterruptionLifetime - age
                : TimeSpan.Zero);

        return new PendingFocusSessionInfo(
            session.Id,
            session.ProfileCode,
            session.TaskTitleSnapshot,
            session.StartedAtUtc,
            session.InterruptedAtUtc,
            anchor,
            expiresAtUtc,
            age,
            isExpired,
            session.PlannedFocusSeconds,
            session.ElapsedSeconds,
            session.ActiveStudySeconds,
            remainingTime);
    }
}

/// <summary>
/// Handles the Resume recovery command for an interrupted focus session (system-architecture.md §7.2; A6).
/// Resumes an active session or finalizes it as Completed if planned duration was reached at checkpoint.
/// If expired at or beyond 24 hours, automatically abandons the session with partial XP.
/// </summary>
public sealed class ResumeFocusRecoveryHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly RecoverFocusSessionHandler _recoverSession;

    public ResumeFocusRecoveryHandler(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        RecoverFocusSessionHandler recoverSession)
    {
        _profiles = profiles;
        _sessions = sessions;
        _recoverSession = recoverSession;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(
        Guid localProfileId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null || session.LocalProfileId != localProfileId)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (session.State is not SessionState.RecoverableInterrupted
            && session.State is not (SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned))
        {
            return SessionCompletionOutcome.Failed("SESSION_NOT_PENDING", "The focus session is not awaiting recovery.");
        }

        return await _recoverSession.HandleAsync(sessionId, cancellationToken);
    }
}

/// <summary>
/// Handles the Discard recovery command for an interrupted focus session (system-architecture.md §7.2; A6).
/// Abandons the session idempotently and awards partial XP once (0.5 multiplier).
/// Repeated discard returns the existing outcome unchanged.
/// </summary>
public sealed class DiscardFocusRecoveryHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly AbandonFocusSessionHandler _abandonSession;

    public DiscardFocusRecoveryHandler(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        AbandonFocusSessionHandler abandonSession)
    {
        _profiles = profiles;
        _sessions = sessions;
        _abandonSession = abandonSession;
    }

    public async Task<SessionCompletionOutcome> HandleAsync(
        Guid localProfileId,
        Guid sessionId,
        FocusEndReason reason = FocusEndReason.Unknown,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        var session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null || session.LocalProfileId != localProfileId)
        {
            return SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (session.State is not SessionState.RecoverableInterrupted
            && session.State is not (SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned))
        {
            return SessionCompletionOutcome.Failed("SESSION_NOT_PENDING", "The focus session is not awaiting recovery.");
        }

        return await _abandonSession.HandleAsync(sessionId, reason, cancellationToken);
    }
}

/// <summary>
/// Unified application coordinator for the recovery workflow (A6; ADR-0005).
/// Coordinates pending recovery inspection, resume, discard, and start prevention checks.
/// </summary>
public sealed class FocusRecoveryCoordinator
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly ILocalClock _clock;
    private readonly GetPendingFocusSessionHandler _pendingHandler;
    private readonly ResumeFocusRecoveryHandler _resumeHandler;
    private readonly DiscardFocusRecoveryHandler _discardHandler;

    public FocusRecoveryCoordinator(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        ILocalClock clock,
        GetPendingFocusSessionHandler pendingHandler,
        ResumeFocusRecoveryHandler resumeHandler,
        DiscardFocusRecoveryHandler discardHandler)
    {
        _profiles = profiles;
        _sessions = sessions;
        _clock = clock;
        _pendingHandler = pendingHandler;
        _resumeHandler = resumeHandler;
        _discardHandler = discardHandler;
    }

    public Task<PendingFocusSessionInfo?> GetPendingAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        _pendingHandler.HandleAsync(localProfileId, cancellationToken);

    public Task<SessionCompletionOutcome> ResumeAsync(Guid localProfileId, Guid sessionId, CancellationToken cancellationToken = default) =>
        _resumeHandler.HandleAsync(localProfileId, sessionId, cancellationToken);

    public Task<SessionCompletionOutcome> DiscardAsync(Guid localProfileId, Guid sessionId, FocusEndReason reason = FocusEndReason.Unknown, CancellationToken cancellationToken = default) =>
        _discardHandler.HandleAsync(localProfileId, sessionId, reason, cancellationToken);

    /// <summary>
    /// Checks whether a new session start is permitted or blocked by a pending recoverable session.
    /// Returns true if start is allowed (no unexpired recoverable session pending).
    /// </summary>
    public async Task<bool> CanStartNewSessionAsync(Guid localProfileId, CancellationToken cancellationToken = default)
    {
        var pending = await _sessions.FindActiveByProfileAsync(localProfileId, cancellationToken);
        if (pending is not { State: SessionState.RecoverableInterrupted })
        {
            return true;
        }

        return pending.IsPendingInterruptionExpired(_clock.UtcNow);
    }
}
