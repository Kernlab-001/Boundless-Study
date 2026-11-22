using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Focus-session terminal reward behavior: ECONOMY-1 vectors TV-01..TV-08,
/// TV-11..TV-14, TV-17, TV-19, TV-20 through the real application handlers.
/// </summary>
public class SessionCompletionTests
{
    // ---- Session XP (TV-01..TV-03) ----

    [Fact]
    public async Task TV01_flexible_25_active_minutes_awards_50_session_xp_and_qualifies()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Flexible", plannedMinutes: 25);

        var outcome = harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);

        Assert.True(outcome.Success);
        Assert.Equal(50, outcome.XpAwarded);
        Assert.True(outcome.IsStreakQualified);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == $"SessionCompletion:{sessionId}:XP" && e.Amount == 50);
    }

    [Fact]
    public async Task TV02_balanced_22_active_minutes_awards_66()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = harness.Complete(sessionId, activeMinutes: 22, plannedMinutes: 25);

        Assert.Equal(66, outcome.XpAwarded);
        Assert.True(outcome.IsStreakQualified);
    }

    [Fact]
    public async Task TV03_strict_30_active_minutes_awards_120()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Strict", plannedMinutes: 30);

        var outcome = harness.Complete(sessionId, activeMinutes: 30, plannedMinutes: 30);

        Assert.Equal(120, outcome.XpAwarded);
        Assert.True(outcome.IsStreakQualified);
    }

    // ---- Partial awards (TV-04, TV-05, TV-15) ----

    [Fact]
    public async Task TV04_ended_early_balanced_12_minutes_awards_18_and_is_not_qualified()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = await harness.EndEarly.HandleAsync(sessionId, 12 * 60, 12 * 60, 0, 0, FocusEndReason.UserEnded);

        Assert.True(outcome.Success);
        Assert.Equal(18, outcome.XpAwarded);
        Assert.False(outcome.IsStreakQualified);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == $"SessionPartial:{sessionId}:XP" && e.Amount == 18);
        Assert.DoesNotContain(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionCompletion);
    }

    [Fact]
    public async Task TV05_abandoned_balanced_20_minutes_awards_30_and_is_not_qualified()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        Assert.True((await harness.Checkpoint.HandleAsync(sessionId, 20 * 60, 20 * 60, 0, 0, 0, 0, 0)).Success);
        Assert.True((await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash)).Success);
        var outcome = await harness.Abandon.HandleAsync(sessionId, FocusEndReason.Unknown);

        Assert.Equal(30, outcome.XpAwarded);
        Assert.False(outcome.IsStreakQualified);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == $"SessionPartial:{sessionId}:XP" && e.Amount == 30);
    }

    [Fact]
    public async Task TV15_rounding_floors_to_19()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = await harness.EndEarly.HandleAsync(sessionId, 13 * 60, 13 * 60, 0, 0, FocusEndReason.UserEnded);

        Assert.Equal(19, outcome.XpAwarded);
    }

    // ---- Recovery (TV-06) ----

    [Fact]
    public async Task TV06_recovery_to_completion_awards_75_once_with_no_separate_partial_award()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        Assert.True((await harness.Checkpoint.HandleAsync(sessionId, 25 * 60, 25 * 60, 0, 0, 0, 0, 0)).Success);
        Assert.True((await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.PowerLoss)).Success);

        var outcome = await harness.RecoverSession.HandleAsync(sessionId);

        Assert.True(outcome.Success);
        Assert.Equal(75, outcome.XpAwarded);
        Assert.True(outcome.IsStreakQualified);
        Assert.Single(harness.Ledger.All, e => e.SourceType is LedgerSourceType.SessionCompletion or LedgerSourceType.SessionPartial);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == $"SessionCompletion:{sessionId}:XP");
    }

    [Fact]
    public async Task Recovery_below_the_checkpoint_threshold_returns_to_active_without_award()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        await harness.Checkpoint.HandleAsync(sessionId, 10 * 60, 10 * 60, 0, 0, 0, 0, 0);
        await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash);

        var outcome = await harness.RecoverSession.HandleAsync(sessionId);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.XpAwarded);
        Assert.Empty(harness.Ledger.All);
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Equal(1, session.RecoveryCount);
    }

    // ---- Daily cap (TV-07, TV-08, TV-19, TV-20) ----

    [Fact]
    public async Task TV07_daily_cap_reduces_to_20_and_the_day_total_is_300()
    {
        var harness = new TestHarness();
        harness.SeedPriorSessionXp(280);
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 10);

        var outcome = harness.Complete(sessionId, activeMinutes: 10, plannedMinutes: 10);

        Assert.Equal(20, outcome.XpAwarded);
        Assert.Equal(300, await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today));
    }

    [Fact]
    public async Task TV08_cap_reached_awards_zero_creates_no_session_entry_but_still_qualifies_the_streak()
    {
        var harness = new TestHarness();
        harness.SeedPriorSessionXp(300);
        var sessionId = harness.StartSessionAndGetId("Strict", plannedMinutes: 30);

        var outcome = harness.Complete(sessionId, activeMinutes: 30, plannedMinutes: 30);

        Assert.True(outcome.Success);
        Assert.Equal(0, outcome.XpAwarded);
        Assert.True(outcome.IsStreakQualified);

        // No session-XP ledger entry for this session (Amount must be > 0); day total stays 300.
        Assert.DoesNotContain(harness.Ledger.All, e =>
            e.IdempotencyKey == $"SessionCompletion:{sessionId}:XP" || e.IdempotencyKey == $"SessionPartial:{sessionId}:XP");
        Assert.Equal(300, await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today));

        // The qualified day is still derived from the completed session, not the ledger.
        var qualifiedDates = await harness.GetProgress.HandleAsync(harness.Profile.Id);
        Assert.True(qualifiedDates!.CurrentStreakDays >= 1);
    }

    [Fact]
    public async Task TV19_partial_cap_on_early_exit_awards_10()
    {
        var harness = new TestHarness();
        harness.SeedPriorSessionXp(290);
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = await harness.EndEarly.HandleAsync(sessionId, 12 * 60, 12 * 60, 0, 0, FocusEndReason.UserEnded);

        Assert.Equal(10, outcome.XpAwarded);
        Assert.Equal(300, await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today));
    }

    [Fact]
    public async Task TV20_the_cap_applies_per_local_completion_day()
    {
        var harness = new TestHarness();
        harness.SeedPriorSessionXp(300); // day 1 at cap

        var dayOne = harness.StartSessionAndGetId("Balanced", plannedMinutes: 10);
        var dayOneOutcome = harness.Complete(dayOne, activeMinutes: 10, plannedMinutes: 10);
        Assert.Equal(0, dayOneOutcome.XpAwarded);

        // Cross local midnight: the next day has its own 300 XP balance.
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddDays(1);
        var dayTwo = harness.StartSessionAndGetId("Balanced", plannedMinutes: 10);
        var dayTwoOutcome = harness.Complete(dayTwo, activeMinutes: 10, plannedMinutes: 10);

        Assert.Equal(30, dayTwoOutcome.XpAwarded);
        Assert.Equal(30, await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today));
    }

    // ---- Achievements (TV-11) & below-minimum (TV-17) ----

    [Fact]
    public async Task TV11_one_event_unlocks_two_achievements_in_code_order_with_total_305()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 60);
        harness.UnitOfWork.ResetCounts();

        var outcome = harness.Complete(sessionId, activeMinutes: 60, plannedMinutes: 60);

        Assert.True(outcome.Success);
        Assert.Equal(180, outcome.XpAwarded);
        Assert.Equal(new[] { "ACH-001", "ACH-002" }, outcome.AchievementsUnlocked);
        Assert.Equal(305, outcome.TotalXpAfter);
        Assert.Equal(305, await harness.Ledger.SumXpAsync(harness.Profile.Id));

        // All three entries in one commit.
        Assert.Equal(3, harness.Ledger.All.Count);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
        Assert.Equal(0, harness.UnitOfWork.RollbackCount);
    }

    [Fact]
    public async Task ACH002_accumulates_active_minutes_across_completed_ended_early_and_abandoned_sessions()
    {
        var harness = new TestHarness();

        // 30 completed minutes: ACH-001 unlocks, ACH-002 is still 30 minutes short.
        var completed = harness.StartSessionAndGetId("Balanced", plannedMinutes: 30);
        var first = harness.Complete(completed, activeMinutes: 30, plannedMinutes: 30);
        Assert.DoesNotContain("ACH-002", first.AchievementsUnlocked);

        // 20 ended-early minutes: 50 accumulated, still below the 60-minute threshold.
        var endedEarly = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        var second = await harness.EndEarly.HandleAsync(
            endedEarly, 20 * 60, 20 * 60, 0, 0, FocusEndReason.UserEnded);
        Assert.DoesNotContain("ACH-002", second.AchievementsUnlocked);

        // 10 abandoned minutes: 60 accumulated → ACH-002 unlocks with its 75 XP.
        var abandoned = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        Assert.True((await harness.Checkpoint.HandleAsync(
            abandoned, 10 * 60, 10 * 60, 0, 0, 0, 0, 0)).Success);
        Assert.True((await harness.Interrupt.HandleAsync(abandoned, FocusEndReason.AgentCrash)).Success);
        var third = await harness.Abandon.HandleAsync(abandoned, FocusEndReason.Unknown);

        Assert.Contains("ACH-002", third.AchievementsUnlocked);
        Assert.Contains(harness.Ledger.All, e =>
            e.IdempotencyKey == "AchievementUnlock:ACH-002:XP" && e.Amount == 75);
        Assert.NotNull(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-002"));
    }

    [Fact]
    public async Task ACH002_does_not_count_active_time_before_a_session_reaches_a_terminal_state()
    {
        var harness = new TestHarness();

        // A checkpointed but still-recoverable session has recorded 60 minutes, but it
        // has not reached a terminal reward state and must not unlock ACH-002 yet.
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 60);
        Assert.True((await harness.Checkpoint.HandleAsync(
            sessionId, 60 * 60, 60 * 60, 0, 0, 0, 0, 0)).Success);
        Assert.True((await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash)).Success);

        // Task completion triggers achievement evaluation while the session is pending.
        var taskId = harness.CreateTaskAndGetId();
        await harness.CompleteTask.HandleAsync(taskId);

        Assert.DoesNotContain(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-002:XP");
        Assert.Null(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-002"));

        // Abandoning finalizes the recorded active time; ACH-002 unlocks in that transaction.
        var abandoned = await harness.Abandon.HandleAsync(sessionId, FocusEndReason.Unknown);
        Assert.Contains("ACH-002", abandoned.AchievementsUnlocked);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-002:XP");
        Assert.NotNull(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-002"));
    }

    [Fact]
    public async Task TV17_below_10_active_minutes_awards_zero_but_the_session_stays_completed()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 10);

        // 599 active seconds is below the 600-second minimum; the session still completes.
        var below = await harness.CompleteSession.HandleAsync(sessionId, 10 * 60, 599, 0, 0);

        Assert.True(below.Success);
        Assert.Equal(0, below.XpAwarded);
        Assert.False(below.IsStreakQualified);
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Completed, session!.State);
        Assert.DoesNotContain(harness.Ledger.All, e => e.SourceType is LedgerSourceType.SessionCompletion or LedgerSourceType.SessionPartial);
    }

    // ---- Idempotency and retry (TV-12..TV-14) ----

    [Fact]
    public async Task TV13_crash_after_commit_retry_returns_the_existing_entry()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var first = harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);
        var retry = harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);

        Assert.Equal(75, first.XpAwarded);
        Assert.True(retry.Success);
        Assert.False(retry.Changed);
        Assert.Equal(75, retry.XpAwarded);
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionCompletion);

        var totalXp = await harness.Ledger.SumXpAsync(harness.Profile.Id);
        Assert.Equal(75 + 50, totalXp); // session 75 + ACH-001 50
    }

    [Fact]
    public async Task TV14_uncertain_result_retry_returns_the_same_entry_and_total_is_unchanged()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash);
        await harness.Checkpoint.HandleAsync(sessionId, 25 * 60, 25 * 60, 0, 0, 0, 0, 0);

        var first = await harness.RecoverSession.HandleAsync(sessionId);
        var totalAfterFirst = await harness.Ledger.SumXpAsync(harness.Profile.Id);
        var retry = harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);

        Assert.False(retry.Changed);
        Assert.Equal(first.XpAwarded, retry.XpAwarded);
        Assert.Equal(totalAfterFirst, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task A_reward_transaction_never_creates_a_second_session_key()
    {
        // A session has exactly one terminal reward state, so at most one of the two
        // session keys can exist (ECONOMY-1 §8.10).
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);
        await harness.EndEarly.HandleAsync(sessionId, 25 * 60, 25 * 60, 0, 0, FocusEndReason.UserEnded);

        var sessionKeys = harness.Ledger.All
            .Where(e => e.IdempotencyKey.StartsWith("SessionCompletion:") || e.IdempotencyKey.StartsWith("SessionPartial:"))
            .ToArray();
        Assert.Single(sessionKeys);
    }
}
