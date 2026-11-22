namespace BoundlessStudy.Domain;

/// <summary>Reward and achievement enumerations (conceptual-data-model.md §4.12–4.13).</summary>
public enum CurrencyCode
{
    Xp,
    Token,
}

public enum LedgerSourceType
{
    SessionCompletion,
    SessionPartial,
    TaskCompletion,
    AchievementUnlock,
    Adjustment,
}

/// <summary>
/// Achievement criteria types (conceptual-data-model.md §4.13). Immutable for a
/// given AchievementCode: changing eligibility requires a new code
/// (domain-business-rules.md §8.9.1).
/// </summary>
public enum AchievementCriteriaType
{
    SessionCount,
    ActiveMinutes,
    StreakDays,
    StrictSessionCount,
    TaskCount,
}
