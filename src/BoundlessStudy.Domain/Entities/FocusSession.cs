namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// A timed study block (conceptual-data-model.md §4.5, domain-business-rules.md §5).
/// Enforces the approved state machine:
/// Active↔Blocked, Active↔SystemPaused, Active→Completed/EndedEarly,
/// Active/Blocked/SystemPaused→RecoverableInterrupted,
/// RecoverableInterrupted→Active/Completed/Abandoned.
/// Counters are non-negative and ActiveStudySeconds ≤ ElapsedSeconds.
/// </summary>
public class FocusSession
{
    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public Guid? TaskId { get; private set; }

    public Guid? SubjectId { get; private set; }

    public string? TaskTitleSnapshot { get; private set; }

    public string? SubjectNameSnapshot { get; private set; }

    public string ProfileCode { get; private set; } = string.Empty;

    public int PlannedFocusSeconds { get; private set; }

    public SessionState State { get; private set; } = SessionState.Active;

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? EndedAtUtc { get; private set; }

    public DateTime? LastCheckpointAtUtc { get; private set; }

    /// <summary>
    /// When the current unresolved interruption began. Set on the transition to
    /// RecoverableInterrupted, cleared on recovery, and retained for audit once the
    /// session becomes terminal. Legacy rows may be null; the deterministic fallback
    /// is <see cref="UpdatedAtUtc"/> (A6, approved 2026-11-19).
    /// </summary>
    public DateTime? InterruptedAtUtc { get; private set; }

    public int ElapsedSeconds { get; private set; }

    public int ActiveStudySeconds { get; private set; }

    public int IdleSeconds { get; private set; }

    public int ProtectionGapSeconds { get; private set; }

    public int ViolationCount { get; private set; }

    public int ExceptionCount { get; private set; }

    public int RecoveryCount { get; private set; }

    public ProtectionStatus ProtectionStatus { get; private set; } = ProtectionStatus.Unknown;

    public VerificationStatus VerificationStatus { get; private set; } = VerificationStatus.Unverified;

    public FocusEndReason? EndReason { get; private set; }

    public string AppRuleVersion { get; private set; } = string.Empty;

    public string EconomyRuleVersion { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Elapsed seconds at the last checkpoint. Recovery finalizes completion from
    /// this value (§5.2). It is derived from the persisted checkpoint columns so a
    /// reloaded session does not need a duplicate database column (Milestone 2
    /// review A5; conceptual-data-model.md §4.5).
    /// </summary>
    public int? LastCheckpointElapsedSeconds =>
        LastCheckpointAtUtc is not null ? ElapsedSeconds : null;

    private FocusSession()
    {
    }

    /// <summary>Starts a new focus session in the Active state.</summary>
    public static FocusSession Start(
        Guid localProfileId,
        string profileCode,
        int plannedFocusSeconds,
        DateTime startedAtUtc,
        string economyRuleVersion,
        Guid? taskId = null,
        Guid? subjectId = null,
        string? taskTitleSnapshot = null,
        string? subjectNameSnapshot = null,
        ProtectionStatus initialProtectionStatus = ProtectionStatus.Unknown)
    {
        ValueObjects.PlannedFocusSeconds.Create(plannedFocusSeconds);

        if (string.IsNullOrWhiteSpace(profileCode))
        {
            throw new DomainException("SESSION_PROFILE_REQUIRED", "A focus session must record the profile it uses.");
        }

        if (string.IsNullOrWhiteSpace(economyRuleVersion))
        {
            throw new DomainException("SESSION_RULE_VERSIONS_REQUIRED", "A focus session must snapshot its economy rule version.");
        }

        if (taskTitleSnapshot is not null && taskTitleSnapshot.Length > 200)
        {
            throw new DomainException("SESSION_SNAPSHOT_TOO_LONG", "The task title snapshot may be at most 200 characters.");
        }

        if (subjectNameSnapshot is not null && subjectNameSnapshot.Length > 100)
        {
            throw new DomainException("SESSION_SNAPSHOT_TOO_LONG", "The subject name snapshot may be at most 100 characters.");
        }

        return new FocusSession
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            ProfileCode = profileCode,
            PlannedFocusSeconds = plannedFocusSeconds,
            StartedAtUtc = startedAtUtc,
            State = SessionState.Active,
            TaskId = taskId,
            SubjectId = subjectId,
            TaskTitleSnapshot = taskTitleSnapshot,
            SubjectNameSnapshot = subjectNameSnapshot,
            ProtectionStatus = initialProtectionStatus,
            AppRuleVersion = AppRules.Version,
            EconomyRuleVersion = economyRuleVersion,
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
    }

    /// <summary>
    /// Records a checkpoint (§3.6): state, elapsed/active/idle/gap seconds, counters.
    /// Recovery never invents time; the checkpoint is the only source for reconciliation.
    /// </summary>
    public void ApplyCheckpoint(
        DateTime checkpointAtUtc,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        int violationCount,
        int exceptionCount,
        int recoveryCount)
    {
        EnsureNotTerminal("be checkpointed");
        ValidateCounters(elapsedSeconds, activeStudySeconds, idleSeconds, protectionGapSeconds, violationCount, exceptionCount, recoveryCount);

        ElapsedSeconds = elapsedSeconds;
        ActiveStudySeconds = activeStudySeconds;
        IdleSeconds = idleSeconds;
        ProtectionGapSeconds = protectionGapSeconds;
        ViolationCount = violationCount;
        ExceptionCount = exceptionCount;
        RecoveryCount = recoveryCount;
        LastCheckpointAtUtc = checkpointAtUtc;
        Touch(checkpointAtUtc);
    }

    public void SetProtection(ProtectionStatus protectionStatus, VerificationStatus verificationStatus, DateTime atUtc)
    {
        ProtectionStatus = protectionStatus;
        VerificationStatus = verificationStatus;
        Touch(atUtc);
    }

    /// <summary>A blocked application is foreground and the overlay is active (Active → Blocked).</summary>
    public void Block(DateTime atUtc)
    {
        EnsureState(SessionState.Active, nameof(Block));
        State = SessionState.Blocked;
        Touch(atUtc);
    }

    /// <summary>The user returns to study (Blocked → Active).</summary>
    public void Unblock(DateTime atUtc)
    {
        EnsureState(SessionState.Blocked, nameof(Unblock));
        State = SessionState.Active;
        Touch(atUtc);
    }

    /// <summary>Lock, sleep, or suspend (Active → SystemPaused).</summary>
    public void SystemPause(DateTime atUtc)
    {
        EnsureState(SessionState.Active, nameof(SystemPause));
        State = SessionState.SystemPaused;
        Touch(atUtc);
    }

    /// <summary>Resume from lock or suspend (SystemPaused → Active). Downtime is not credited.</summary>
    public void SystemResume(DateTime atUtc)
    {
        EnsureState(SessionState.SystemPaused, nameof(SystemResume));
        State = SessionState.Active;
        Touch(atUtc);
    }

    /// <summary>Agent crash, shutdown, or power loss → RecoverableInterrupted. Checkpoint is preserved.</summary>
    public void MarkRecoverableInterrupted(DateTime atUtc, FocusEndReason interruptionReason)
    {
        if (State is not (SessionState.Active or SessionState.Blocked or SessionState.SystemPaused))
        {
            throw new DomainException("SESSION_TRANSITION_INVALID", $"A session in state {State} cannot become RecoverableInterrupted.");
        }

        if (interruptionReason is FocusEndReason.Completed or FocusEndReason.UserEnded)
        {
            throw new DomainException("SESSION_INTERRUPT_REASON_INVALID", "An interruption must record an involuntary reason.");
        }

        State = SessionState.RecoverableInterrupted;
        EndReason = interruptionReason;
        InterruptedAtUtc = atUtc;
        Touch(atUtc);
    }

    /// <summary>Recovers with the last checkpoint (RecoverableInterrupted → Active). Downtime is not credited.</summary>
    public void Recover(DateTime atUtc)
    {
        if (State is not SessionState.RecoverableInterrupted)
        {
            throw new DomainException("SESSION_TRANSITION_INVALID", "Only a RecoverableInterrupted session can be recovered.");
        }

        State = SessionState.Active;
        EndReason = null;
        InterruptedAtUtc = null;
        RecoveryCount++;
        Touch(atUtc);
    }

    /// <summary>
    /// Normal completion (Active → Completed). Requires elapsed ≥ planned
    /// (conceptual-data-model.md §4.5; §3.5). Counters come from the checkpoint.
    /// </summary>
    public void Complete(DateTime completedAtUtc, int elapsedSeconds, int activeStudySeconds, int idleSeconds, int protectionGapSeconds)
    {
        EnsureState(SessionState.Active, nameof(Complete));
        ValidateCounters(elapsedSeconds, activeStudySeconds, idleSeconds, protectionGapSeconds, ViolationCount, ExceptionCount, RecoveryCount);

        if (elapsedSeconds < PlannedFocusSeconds)
        {
            throw new DomainException("SESSION_COMPLETION_TOO_EARLY", "A session can complete only when elapsed time reaches the planned duration.");
        }

        ElapsedSeconds = elapsedSeconds;
        ActiveStudySeconds = activeStudySeconds;
        IdleSeconds = idleSeconds;
        ProtectionGapSeconds = protectionGapSeconds;
        State = SessionState.Completed;
        EndReason = FocusEndReason.Completed;
        EndedAtUtc = completedAtUtc;
        Touch(completedAtUtc);
    }

    /// <summary>
    /// Finalizes completion from a recovery checkpoint that already met the planned
    /// duration (RecoverableInterrupted → Completed; §5.2). Uses checkpoint values;
    /// no time is invented for the outage.
    /// </summary>
    public void CompleteFromCheckpoint(DateTime completedAtUtc)
    {
        if (State is not SessionState.RecoverableInterrupted)
        {
            throw new DomainException("SESSION_TRANSITION_INVALID", "Completion from recovery requires a RecoverableInterrupted session.");
        }

        if (LastCheckpointElapsedSeconds is not { } checkpointElapsed || checkpointElapsed < PlannedFocusSeconds)
        {
            throw new DomainException("SESSION_COMPLETION_TOO_EARLY", "The recovery checkpoint must already meet the planned duration.");
        }

        ElapsedSeconds = checkpointElapsed;
        State = SessionState.Completed;
        EndReason = FocusEndReason.Completed;
        EndedAtUtc = completedAtUtc;
        Touch(completedAtUtc);
    }

    /// <summary>User ends the session early (Active → EndedEarly). Reason must not be Completed.</summary>
    public void EndEarly(DateTime endedAtUtc, int elapsedSeconds, int activeStudySeconds, int idleSeconds, int protectionGapSeconds, FocusEndReason endReason)
    {
        EnsureState(SessionState.Active, nameof(EndEarly));
        ValidateCounters(elapsedSeconds, activeStudySeconds, idleSeconds, protectionGapSeconds, ViolationCount, ExceptionCount, RecoveryCount);

        if (endReason is FocusEndReason.Completed)
        {
            throw new DomainException("SESSION_END_REASON_INVALID", "An ended-early session must record a reason other than Completed.");
        }

        ElapsedSeconds = elapsedSeconds;
        ActiveStudySeconds = activeStudySeconds;
        IdleSeconds = idleSeconds;
        ProtectionGapSeconds = protectionGapSeconds;
        State = SessionState.EndedEarly;
        EndReason = endReason;
        EndedAtUtc = endedAtUtc;
        Touch(endedAtUtc);
    }

    /// <summary>Expired, declined, or unrecoverable (RecoverableInterrupted → Abandoned).</summary>
    public void Abandon(DateTime abandonedAtUtc, FocusEndReason abandonmentReason)
    {
        if (State is not SessionState.RecoverableInterrupted)
        {
            throw new DomainException("SESSION_TRANSITION_INVALID", "Only a RecoverableInterrupted session can be abandoned.");
        }

        if (abandonmentReason is FocusEndReason.Completed)
        {
            throw new DomainException("SESSION_END_REASON_INVALID", "An abandoned session must record a reason other than Completed.");
        }

        State = SessionState.Abandoned;
        EndReason = abandonmentReason;
        EndedAtUtc = abandonedAtUtc;
        Touch(abandonedAtUtc);
    }

    public bool IsTerminal =>
        State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned;

    public bool IsRewardTerminal =>
        State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned;

    public bool IsActive =>
        State is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused;

    /// <summary>
    /// The timestamp used to measure unresolved interruption age. New rows use the
    /// dedicated <see cref="InterruptedAtUtc"/> column; a legacy row without it falls
    /// back to <see cref="UpdatedAtUtc"/>, which is never earlier than the interruption
    /// transition and therefore cannot expire a session prematurely.
    /// </summary>
    public DateTime EffectiveInterruptionAnchorUtc => InterruptedAtUtc ?? UpdatedAtUtc;

    /// <summary>
    /// Age of the current unresolved interruption at <paramref name="nowUtc"/>. A
    /// future or equal anchor (clock skew or invalid timestamp) yields zero, so an
    /// invalid timestamp never causes immediate abandonment. A non-interrupted
    /// session also yields zero.
    /// </summary>
    public TimeSpan PendingInterruptionAge(DateTime nowUtc)
    {
        if (State is not SessionState.RecoverableInterrupted)
        {
            return TimeSpan.Zero;
        }

        var anchor = EffectiveInterruptionAnchorUtc;
        return nowUtc > anchor ? nowUtc - anchor : TimeSpan.Zero;
    }

    /// <summary>
    /// True when the session is unresolved at or beyond the approved 24-hour
    /// lifetime. The boundary is inclusive: at exactly 24:00:00 the session is
    /// expired, consistent with §5.2 ("less than 24 hours old" may recover).
    /// </summary>
    public bool IsPendingInterruptionExpired(DateTime nowUtc) =>
        State is SessionState.RecoverableInterrupted
        && PendingInterruptionAge(nowUtc) >= SessionRecoveryPolicy.PendingInterruptionLifetime;

    /// <summary>Snapshot values are stable; renaming never rewrites them (§9.3).</summary>
    public bool HasTaskLink => TaskId is not null;

    private static void ValidateCounters(
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        int violationCount,
        int exceptionCount,
        int recoveryCount)
    {
        if (elapsedSeconds < 0 || activeStudySeconds < 0 || idleSeconds < 0 || protectionGapSeconds < 0
            || violationCount < 0 || exceptionCount < 0 || recoveryCount < 0)
        {
            throw new DomainException("SESSION_COUNTER_NEGATIVE", "Session durations and counters cannot be negative.");
        }

        if (activeStudySeconds > elapsedSeconds)
        {
            throw new DomainException("SESSION_ACTIVE_EXCEEDS_ELAPSED", "Active-study time cannot exceed elapsed time.");
        }
    }

    private void EnsureState(SessionState required, string operation)
    {
        if (State != required)
        {
            throw new DomainException("SESSION_TRANSITION_INVALID", $"{operation} requires the {required} state, but the session is {State}.");
        }
    }

    private void EnsureNotTerminal(string operation)
    {
        if (IsTerminal)
        {
            throw new DomainException("SESSION_TERMINAL", $"A terminal session cannot {operation}.");
        }
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
