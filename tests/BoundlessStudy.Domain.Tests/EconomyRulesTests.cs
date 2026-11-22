using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

/// <summary>
/// ECONOMY-1 frozen numerical expectations (domain-business-rules.md §8.12,
/// vectors TV-01..TV-20 where they are pure economy computations). Exact values —
/// a test may not substitute a live-tuned value (§8.2).
/// </summary>
public class EconomyRulesTests
{
    [Fact]
    public void Version_is_frozen()
    {
        Assert.Equal("ECONOMY-1", EconomyRules.Version);
    }

    [Theory]
    [InlineData("Flexible", 2)]
    [InlineData("Balanced", 3)]
    [InlineData("Strict", 4)]
    public void Rates_match_ECONOMY_1(string profileCode, int expectedRate)
    {
        Assert.Equal(expectedRate, EconomyRules.RateForProfile(profileCode));
    }

    [Fact]
    public void Unknown_profile_has_no_rate()
    {
        Assert.Throws<DomainException>(() => EconomyRules.RateForProfile("Turbo"));
    }

    [Fact]
    public void TV01_normal_completion_flexible_25_active_minutes_awards_50()
    {
        var xp = EconomyRules.ComputeSessionXp(25 * 60, "Flexible", SessionState.Completed);
        Assert.Equal(50, xp);
        Assert.True(EconomyRules.IsStreakQualified(SessionState.Completed, 25 * 60));
    }

    [Fact]
    public void TV02_normal_completion_balanced_22_active_minutes_awards_66()
    {
        Assert.Equal(66, EconomyRules.ComputeSessionXp(22 * 60, "Balanced", SessionState.Completed));
    }

    [Fact]
    public void TV03_normal_completion_strict_30_active_minutes_awards_120()
    {
        Assert.Equal(120, EconomyRules.ComputeSessionXp(30 * 60, "Strict", SessionState.Completed));
    }

    [Fact]
    public void TV04_ended_early_balanced_12_active_minutes_awards_18()
    {
        var xp = EconomyRules.ComputeSessionXp(12 * 60, "Balanced", SessionState.EndedEarly);
        Assert.Equal(18, xp);
        Assert.False(EconomyRules.IsStreakQualified(SessionState.EndedEarly, 12 * 60));
    }

    [Fact]
    public void TV05_abandoned_balanced_20_active_minutes_awards_30()
    {
        var xp = EconomyRules.ComputeSessionXp(20 * 60, "Balanced", SessionState.Abandoned);
        Assert.Equal(30, xp);
        Assert.False(EconomyRules.IsStreakQualified(SessionState.Abandoned, 20 * 60));
    }

    [Fact]
    public void TV15_rounding_floors_the_half_xp()
    {
        // 13 active min × 3 × 0.5 = 19.5 → 19 XP.
        Assert.Equal(19, EconomyRules.ComputeSessionXp(13 * 60, "Balanced", SessionState.EndedEarly));
    }

    [Fact]
    public void TV17_below_minimum_active_time_awards_zero_and_is_not_qualified()
    {
        var xp = EconomyRules.ComputeSessionXp(599, "Balanced", SessionState.Completed);
        Assert.Equal(0, xp);
        Assert.False(EconomyRules.IsStreakQualified(SessionState.Completed, 599));
    }

    [Fact]
    public void TV17_boundary_exactly_600_active_seconds_qualifies()
    {
        Assert.Equal(0 + 30, EconomyRules.ComputeSessionXp(600, "Balanced", SessionState.Completed));
        Assert.True(EconomyRules.IsStreakQualified(SessionState.Completed, 600));
    }

    [Fact]
    public void TV07_daily_cap_reduces_the_award_to_the_remaining_balance()
    {
        var computed = EconomyRules.ComputeSessionXp(10 * 60, "Balanced", SessionState.Completed); // 30
        Assert.Equal(20, EconomyRules.ApplyDailySessionCap(computed, priorSessionXpOnLocalDate: 280));
    }

    [Fact]
    public void TV08_cap_reached_awards_zero_but_stays_streak_qualified()
    {
        var computed = EconomyRules.ComputeSessionXp(30 * 60, "Strict", SessionState.Completed); // 120
        Assert.Equal(0, EconomyRules.ApplyDailySessionCap(computed, priorSessionXpOnLocalDate: 300));
        // TV-08: the session still qualifies the day for the streak.
        Assert.True(EconomyRules.IsStreakQualified(SessionState.Completed, 30 * 60));
    }

    [Fact]
    public void TV19_partial_cap_on_early_exit()
    {
        var computed = EconomyRules.ComputeSessionXp(12 * 60, "Balanced", SessionState.EndedEarly); // 18
        Assert.Equal(10, EconomyRules.ApplyDailySessionCap(computed, priorSessionXpOnLocalDate: 290));
    }

    [Fact]
    public void TV06_recovery_completion_uses_the_multiplier_once()
    {
        // A recovered-and-completed session computes like a normal completion: 25 min × 3 = 75, once.
        Assert.Equal(75, EconomyRules.ComputeSessionXp(25 * 60, "Balanced", SessionState.Completed));
    }

    [Fact]
    public void Non_reward_terminal_states_have_no_multiplier()
    {
        Assert.Throws<DomainException>(() => EconomyRules.MultiplierForOutcome(SessionState.Active));
        Assert.Throws<DomainException>(() => EconomyRules.MultiplierForOutcome(SessionState.RecoverableInterrupted));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(499, 1)]
    [InlineData(500, 2)]
    [InlineData(24_499, 49)]
    [InlineData(24_500, 50)]
    [InlineData(25_000, 50)]
    [InlineData(25_001, 50)]
    public void TV16_level_boundaries_match_the_frozen_curve(long totalXp, int expectedLevel)
    {
        Assert.Equal(expectedLevel, EconomyRules.LevelForTotalXp(totalXp));
    }

    [Fact]
    public void Cap_is_300_and_task_xp_is_25()
    {
        Assert.Equal(300, EconomyRules.DailySessionXpCap);
        Assert.Equal(25, EconomyRules.TaskCompletionXp);
    }

    [Fact]
    public void Negative_prior_day_total_is_rejected()
    {
        Assert.Throws<DomainException>(() => EconomyRules.ApplyDailySessionCap(30, -1));
    }
}
