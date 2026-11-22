namespace BoundlessStudy.Application.Results;

using BoundlessStudy.Domain;

/// <summary>Stable machine-readable use-case error codes for the UI error contract.</summary>
public static class UseCaseErrorCodes
{
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string SubjectNotFound = "SUBJECT_NOT_FOUND";
    public const string TaskNotFound = "TASK_NOT_FOUND";
    public const string TaskDeleted = "TASK_DELETED";
    public const string FocusProfileNotFound = "FOCUS_PROFILE_NOT_FOUND";
    public const string ActiveFocusSessionExists = "ACTIVE_FOCUS_SESSION_EXISTS";
    public const string RecoverableSessionPending = "RECOVERABLE_SESSION_PENDING";
    public const string ActiveBreakSessionExists = "ACTIVE_BREAK_SESSION_EXISTS";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionNotActive = "SESSION_NOT_ACTIVE";
    public const string InvalidTransition = "INVALID_TRANSITION";
    public const string DuplicateRule = "DUPLICATE_RULE";
    public const string ProtectedTarget = "BLOCK_TARGET_PROTECTED";
    public const string ExceptionLimitReached = "EXCEPTION_LIMIT_REACHED";
    public const string DurationOutOfRange = "DURATION_OUT_OF_RANGE";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string DomainRuleViolation = "DOMAIN_RULE_VIOLATION";
}

/// <summary>A stable error code plus a user-facing message.</summary>
public sealed record UseCaseError(string Code, string Message);

/// <summary>Common success signal for results produced by a mutating command.</summary>
public interface ICommandOutcome
{
    bool Success { get; }
}

/// <summary>
/// Result of a use case. Success carries nothing extra by default; Failed carries a
/// stable code. Handlers also return success-with-no-change for idempotent retries
/// (§8.10): the outcome is success, and the payload flags whether anything changed.
/// </summary>
public sealed record UseCaseResult(bool Success, UseCaseError? Error = null, bool Changed = true) : ICommandOutcome
{
    public static UseCaseResult Ok(bool changed = true) => new(true, null, changed);

    public static UseCaseResult Fail(string code, string message) => new(false, new UseCaseError(code, message), false);

    public static UseCaseResult FromDomainException(DomainException exception) =>
        Fail(exception.Code, exception.Message);
}

/// <summary>Result of a use case that creates an entity: returns the new id.</summary>
public sealed record EntityResult(Guid? Id, bool Success, UseCaseError? Error = null) : ICommandOutcome
{
    public static EntityResult Ok(Guid id) => new(id, true, null);

    public static EntityResult Fail(string code, string message) => new(null, false, new UseCaseError(code, message));
}

/// <summary>Result of a reward-bearing use case, with derived level feedback (§8.7).</summary>
public sealed record RewardResult(long TotalXpAfter, int LevelBefore, int LevelAfter, bool Changed, UseCaseError? Error = null) : ICommandOutcome
{
    public bool LevelUp => LevelAfter > LevelBefore;

    public bool Success => Error is null;
}

/// <summary>Result of completing a focus session's terminal reward transaction.</summary>
public sealed record SessionCompletionOutcome(
    bool Success,
    UseCaseError? Error,
    bool Changed,
    int XpAwarded,
    long TotalXpAfter,
    int LevelBefore,
    int LevelAfter,
    bool IsStreakQualified,
    IReadOnlyList<string> AchievementsUnlocked) : ICommandOutcome
{
    public bool LevelUp => LevelAfter > LevelBefore;

    public static SessionCompletionOutcome Awarded(
        int xpAwarded,
        long totalXpAfter,
        int levelBefore,
        int levelAfter,
        bool streakQualified,
        IReadOnlyList<string> achievements) => new(
        true, null, true, xpAwarded, totalXpAfter, levelBefore, levelAfter, streakQualified, achievements);

    public static SessionCompletionOutcome NoChange(long totalXpAfter, int levelAfter) => new(
        true, null, false, 0, totalXpAfter, levelAfter, levelAfter, false, Array.Empty<string>());

    public static SessionCompletionOutcome Failed(string code, string message) => new(
        false, new UseCaseError(code, message), false, 0, 0, 0, 0, false, Array.Empty<string>());
}
