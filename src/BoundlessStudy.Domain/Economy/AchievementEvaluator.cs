namespace BoundlessStudy.Domain.Economy;

/// <summary>
/// Snapshot of the counters an achievement evaluation is based on. All values are
/// derived from primary data (sessions, ledger, tasks) before evaluation.
/// </summary>
public readonly record struct AchievementProgress(
    int QualifiedSessionCount,
    int TotalActiveStudyMinutes,
    int CurrentStreakDays,
    int StrictQualifiedSessionCount,
    int CompletedTaskCount);

/// <summary>
/// Achievement evaluation (ECONOMY-1 §8.6): unlocked once per profile, ascending
/// achievement-code order when one event satisfies several, and a duplicate request
/// is success-with-no-change. Pure function; persistence is the caller's transaction.
/// </summary>
public static class AchievementEvaluator
{
    /// <summary>
    /// Returns the codes of enabled achievements whose criteria are now met and that
    /// the profile has not unlocked yet, in ascending code order (§8.6).
    /// </summary>
    public static IReadOnlyList<string> EvaluateNewUnlocks(
        IReadOnlyCollection<Entities.AchievementDefinition> definitions,
        IReadOnlyCollection<string> alreadyUnlockedCodes,
        AchievementProgress progress)
    {
        var unlocked = new HashSet<string>(alreadyUnlockedCodes, StringComparer.Ordinal);

        return definitions
            .Where(d => d.IsEnabled)
            .Where(d => !unlocked.Contains(d.Code))
            .Where(d => IsCriteriaMet(d, progress))
            .Select(d => d.Code)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCriteriaMet(Entities.AchievementDefinition definition, AchievementProgress progress) =>
        definition.CriteriaType switch
        {
            AchievementCriteriaType.SessionCount => progress.QualifiedSessionCount >= definition.Threshold,
            AchievementCriteriaType.ActiveMinutes => progress.TotalActiveStudyMinutes >= definition.Threshold,
            AchievementCriteriaType.StreakDays => progress.CurrentStreakDays >= definition.Threshold,
            AchievementCriteriaType.StrictSessionCount => progress.StrictQualifiedSessionCount >= definition.Threshold,
            AchievementCriteriaType.TaskCount => progress.CompletedTaskCount >= definition.Threshold,
            _ => false,
        };
}

/// <summary>
/// The five approved MVP achievement seeds with their exact frozen values
/// (domain-business-rules.md §8.9, `ECONOMY-1`). Functional copy only; badge art
/// is blocked pending visual references (ADR-0008).
/// </summary>
public static class MvpAchievements
{
    public const string FirstFocusCode = "ACH-001";
    public const string OneHourCode = "ACH-002";
    public const string ThreeDayRhythmCode = "ACH-003";
    public const string DeepWorkCode = "ACH-004";
    public const string TaskFinisherCode = "ACH-005";

    public static IReadOnlyList<Entities.AchievementDefinition> Seed() =>
    [
        Entities.AchievementDefinition.Create(
            FirstFocusCode,
            "First Focus",
            "Complete the first qualified focus session.",
            AchievementCriteriaType.SessionCount,
            threshold: 1,
            xpAward: 50,
            EconomyRules.Version),

        Entities.AchievementDefinition.Create(
            OneHourCode,
            "One Hour",
            "Accumulate 60 verified active-study minutes.",
            AchievementCriteriaType.ActiveMinutes,
            threshold: 60,
            xpAward: 75,
            EconomyRules.Version),

        Entities.AchievementDefinition.Create(
            ThreeDayRhythmCode,
            "Three-Day Rhythm",
            "Reach a 3-day streak.",
            AchievementCriteriaType.StreakDays,
            threshold: 3,
            xpAward: 100,
            EconomyRules.Version),

        Entities.AchievementDefinition.Create(
            DeepWorkCode,
            "Deep Work",
            "Complete 5 Strict focus sessions with at least 10 active minutes.",
            AchievementCriteriaType.StrictSessionCount,
            threshold: 5,
            xpAward: 100,
            EconomyRules.Version),

        Entities.AchievementDefinition.Create(
            TaskFinisherCode,
            "Task Finisher",
            "Complete 10 tasks.",
            AchievementCriteriaType.TaskCount,
            threshold: 10,
            xpAward: 75,
            EconomyRules.Version),
    ];
}


