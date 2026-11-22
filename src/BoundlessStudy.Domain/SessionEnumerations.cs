namespace BoundlessStudy.Domain;

/// <summary>Focus session lifecycle states (conceptual-data-model.md §7, domain-business-rules.md §5).</summary>
public enum SessionState
{
    Active,
    Blocked,
    SystemPaused,
    Completed,
    EndedEarly,
    RecoverableInterrupted,
    Abandoned,
}

/// <summary>Focus session end reasons (conceptual-data-model.md §7).</summary>
public enum FocusEndReason
{
    Completed,
    UserEnded,
    AgentCrash,
    Shutdown,
    PowerLoss,
    OverlayFailure,
    EnforcementFailure,
    Unknown,
}

/// <summary>Break session lifecycle states (conceptual-data-model.md §4.6).</summary>
public enum BreakState
{
    Active,
    Completed,
    EndedEarly,
    Interrupted,
}

/// <summary>Break session end reasons (conceptual-data-model.md §4.6).</summary>
public enum BreakEndReason
{
    Completed,
    UserEnded,
    LockOrSleepTimeout,
    Shutdown,
    Unknown,
}

/// <summary>Which kind of timed session an event belongs to (conceptual-data-model.md §4.7).</summary>
public enum SessionType
{
    Focus,
    Break,
}

/// <summary>
/// Append-only session event types (conceptual-data-model.md §4.7).
/// <para>
/// <see cref="Interrupted"/> and <see cref="Abandoned"/> are additive values retained
/// by product approval (A4, 2026-11-19). They are part of the persisted CHECK
/// constraint and existing event history remains readable; do not rename or remove
/// them without a table-rebuild migration.
/// </para>
/// </summary>
public enum SessionEventType
{
    Start,
    Blocked,
    Unblocked,
    IdleStarted,
    IdleEnded,
    SystemPaused,
    SystemResumed,
    BreakStarted,
    BreakEnded,
    ExceptionGranted,
    ProtectionDegraded,
    ProtectionRestored,
    Recovered,
    Completed,
    EndedEarly,
    Interrupted,
    Abandoned,
    EnforcementFailed,
    RewardCreated,
    AchievementUnlocked,
}

/// <summary>Early-exit friction modes (domain-business-rules.md §4.1).</summary>
public enum EarlyExitMode
{
    Immediate,
    ConfirmationCooldown,
    TypedConfirmationCooldown,
}

/// <summary>Protection status vocabulary (domain-business-rules.md §7.4). "On" is not "protected".</summary>
public enum ProtectionStatus
{
    NotRequired,
    NotConfigured,
    Protected,
    Degraded,
    Unavailable,
    Unknown,
}

/// <summary>Verification status (conceptual-data-model.md §7).</summary>
public enum VerificationStatus
{
    Verified,
    Degraded,
    Unverified,
}
