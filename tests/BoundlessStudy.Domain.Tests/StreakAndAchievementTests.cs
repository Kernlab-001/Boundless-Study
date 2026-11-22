using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

public class StreakCalculatorTests
{
    private static readonly DateOnly D1 = new(2026, 11, 9);  // Monday
    private static readonly DateOnly D2 = new(2026, 11, 10);
    private static readonly DateOnly D3 = new(2026, 11, 11);
    private static readonly DateOnly D5 = new(2026, 11, 13); // gap on the 12th

    [Fact]
    public void Empty_history_has_no_streak()
    {
        Assert.Equal(0, StreakCalculator.CurrentStreak([], D3));
        Assert.Equal(0, StreakCalculator.LongestStreak([]));
    }

    [Fact]
    public void Streak_counts_consecutive_days_through_today()
    {
        Assert.Equal(3, StreakCalculator.CurrentStreak([D1, D2, D3], D3));
    }

    [Fact]
    public void Unqualified_today_does_not_erase_yesterdays_streak()
    {
        // Last qualified day was D3, and "today" is the next day: the streak still counts.
        Assert.Equal(3, StreakCalculator.CurrentStreak([D1, D2, D3], D3.AddDays(1)));
    }

    [Fact]
    public void A_missed_day_resets_the_streak_when_today_qualifies_again()
    {
        // Gap: nothing on the 12th; a qualification lands on D5 → streak becomes 1 (§8.8).
        Assert.Equal(1, StreakCalculator.CurrentStreak([D1, D2, D3, D5], D5));
    }

    [Fact]
    public void Streak_is_zero_when_the_last_qualified_day_is_two_days_back()
    {
        Assert.Equal(0, StreakCalculator.CurrentStreak([D1, D2, D3], D5.AddDays(1)));
    }

    [Fact]
    public void Future_dates_never_extend_a_streak()
    {
        Assert.Equal(2, StreakCalculator.CurrentStreak([D1, D2, D3], D2));
    }

    [Fact]
    public void Longest_streak_finds_the_longest_run()
    {
        Assert.Equal(3, StreakCalculator.LongestStreak([D1, D2, D3, D5]));
        Assert.Equal(1, StreakCalculator.LongestStreak([D1, D5]));
    }

    [Fact]
    public void Duplicate_dates_are_collapsed()
    {
        Assert.Equal(3, StreakCalculator.LongestStreak([D1, D1, D2, D3]));
        Assert.Equal(3, StreakCalculator.CurrentStreak([D1, D1, D2, D3], D3));
    }
}

/// <summary>Achievement evaluation and the five frozen MVP seeds (§8.6, §8.9).</summary>
public class AchievementEvaluatorTests
{
    private static readonly AchievementProgress Zero = new(0, 0, 0, 0, 0);

    private static readonly IReadOnlyList<AchievementDefinition> Seeds = MvpAchievements.Seed();

    [Fact]
    public void The_five_mvp_seeds_match_ECONOMY_1_exactly()
    {
        Assert.Equal(5, Seeds.Count);

        Assert.Equal(MvpAchievements.FirstFocusCode, Seeds[0].Code);
        Assert.Equal(AchievementCriteriaType.SessionCount, Seeds[0].CriteriaType);
        Assert.Equal(1, Seeds[0].Threshold);
        Assert.Equal(50, Seeds[0].XpAward);
        Assert.Equal("First Focus", Seeds[0].Name);

        Assert.Equal(MvpAchievements.OneHourCode, Seeds[1].Code);
        Assert.Equal(AchievementCriteriaType.ActiveMinutes, Seeds[1].CriteriaType);
        Assert.Equal(60, Seeds[1].Threshold);
        Assert.Equal(75, Seeds[1].XpAward);

        Assert.Equal(MvpAchievements.ThreeDayRhythmCode, Seeds[2].Code);
        Assert.Equal(AchievementCriteriaType.StreakDays, Seeds[2].CriteriaType);
        Assert.Equal(3, Seeds[2].Threshold);
        Assert.Equal(100, Seeds[2].XpAward);

        Assert.Equal(MvpAchievements.DeepWorkCode, Seeds[3].Code);
        Assert.Equal(AchievementCriteriaType.StrictSessionCount, Seeds[3].CriteriaType);
        Assert.Equal(5, Seeds[3].Threshold);
        Assert.Equal(100, Seeds[3].XpAward);

        Assert.Equal(MvpAchievements.TaskFinisherCode, Seeds[4].Code);
        Assert.Equal(AchievementCriteriaType.TaskCount, Seeds[4].CriteriaType);
        Assert.Equal(10, Seeds[4].Threshold);
        Assert.Equal(75, Seeds[4].XpAward);

        Assert.All(Seeds, d => Assert.Equal("ECONOMY-1", d.EconomyRuleVersion));
    }

    [Fact]
    public void TV11_one_event_satisfying_two_achievements_lists_both_in_code_order()
    {
        var progress = new AchievementProgress(
            QualifiedSessionCount: 1,
            TotalActiveStudyMinutes: 60,
            CurrentStreakDays: 1,
            StrictQualifiedSessionCount: 0,
            CompletedTaskCount: 0);

        var unlocked = AchievementEvaluator.EvaluateNewUnlocks(Seeds, [], progress);

        Assert.Equal(new[] { "ACH-001", "ACH-002" }, unlocked);
    }

    [Fact]
    public void ACH002_requires_a_full_60_accumulated_minutes()
    {
        var below = new AchievementProgress(1, 59, 1, 0, 0);
        Assert.DoesNotContain("ACH-002", AchievementEvaluator.EvaluateNewUnlocks(Seeds, ["ACH-001"], below));

        var atThreshold = new AchievementProgress(1, 60, 1, 0, 0);
        Assert.Contains("ACH-002", AchievementEvaluator.EvaluateNewUnlocks(Seeds, ["ACH-001"], atThreshold));
    }

    [Fact]
    public void Already_unlocked_achievements_are_never_returned_again()
    {
        var progress = new AchievementProgress(1, 60, 0, 0, 0);
        var unlocked = AchievementEvaluator.EvaluateNewUnlocks(Seeds, ["ACH-001", "ACH-002"], progress);
        Assert.Empty(unlocked);
    }

    [Fact]
    public void Disabled_definitions_never_unlock()
    {
        var disabled = AchievementDefinition.Create("ACH-090", "Off", "disabled", AchievementCriteriaType.SessionCount, 1, 10, "ECONOMY-1", isEnabled: false);
        var progress = new AchievementProgress(1, 0, 0, 0, 0);
        Assert.Empty(AchievementEvaluator.EvaluateNewUnlocks([disabled], [], progress));
    }

    [Fact]
    public void Strict_sessions_only_count_for_ach_004()
    {
        var progress = new AchievementProgress(5, 0, 0, StrictQualifiedSessionCount: 2, CompletedTaskCount: 0);
        var unlocked = AchievementEvaluator.EvaluateNewUnlocks(Seeds, [], progress);
        Assert.Equal(["ACH-001"], unlocked); // 5 qualified sessions, but only 2 Strict

        var deep = progress with { StrictQualifiedSessionCount = 5 };
        unlocked = AchievementEvaluator.EvaluateNewUnlocks(Seeds, ["ACH-001"], deep);
        Assert.Equal(["ACH-004"], unlocked);
    }

    [Fact]
    public void Task_count_and_streak_criteria()
    {
        var tenTasks = Zero with { CompletedTaskCount = 10 };
        Assert.Equal(["ACH-005"], AchievementEvaluator.EvaluateNewUnlocks(Seeds, [], tenTasks));

        var threeDayStreak = Zero with { CurrentStreakDays = 3 };
        Assert.Equal(["ACH-003"], AchievementEvaluator.EvaluateNewUnlocks(Seeds, [], threeDayStreak));
    }

    [Fact]
    public void Boundaries_are_met_at_exactly_the_threshold()
    {
        var atThreshold = Zero with { CompletedTaskCount = 10 };
        Assert.NotEmpty(AchievementEvaluator.EvaluateNewUnlocks([Seeds[4]], [], atThreshold));

        var belowThreshold = Zero with { CompletedTaskCount = 9 };
        Assert.Empty(AchievementEvaluator.EvaluateNewUnlocks([Seeds[4]], [], belowThreshold));
    }
}
