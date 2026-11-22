using System.Reflection;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Domain.ValueObjects;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Background expiry lifecycle for pending interruptions (A6, approved 2026-11-19):
/// <see cref="PendingRecoverySweep"/> abandons only an expired
/// <c>RecoverableInterrupted</c> session, refuses at 23:59:59, abandons at exactly
/// 24:00:00 and after, ignores future/clock-skew anchors and Active sessions, and
/// never duplicates a terminal reward or event on a repeated sweep or after another
/// actor already resolved the session.
/// </summary>
public class PendingRecoverySweepTests
{
    private const int ActiveMinutes = 10;
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(24);

    #region Baseline Scenarios

    [Fact]
    public async Task No_pending_session_scans_the_profile_and_abandons_nothing()
    {
        var harness = new TestHarness();

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);
        Assert.Empty(harness.Ledger.All);
        Assert.Empty(harness.Events.All);
    }

    [Fact]
    public async Task Active_session_is_never_swept()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddHours(48);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Null(session.EndedAtUtc);
        Assert.Empty(harness.Ledger.All);
        Assert.DoesNotContain(harness.Events.All, e => e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Blocked_and_system_paused_sessions_are_never_swept()
    {
        // 1. Blocked session
        var harness1 = new TestHarness();
        var session1Id = harness1.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        var block = await harness1.BlockState.HandleAsync(session1Id, blocked: true);
        Assert.True(block.Success);
        Assert.Equal(SessionState.Blocked, (await harness1.Sessions.FindAsync(session1Id))!.State);

        harness1.Clock.UtcNow = harness1.Clock.UtcNow.AddHours(48);
        var result1 = await Sweep(harness1).SweepAsync();
        Assert.Equal(1, result1.ProfilesScanned);
        Assert.Equal(0, result1.SessionsAbandoned);
        Assert.Equal(SessionState.Blocked, (await harness1.Sessions.FindAsync(session1Id))!.State);

        // 2. SystemPaused session
        var harness2 = new TestHarness();
        var session2Id = harness2.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        var pause = await harness2.SystemPause.HandleAsync(session2Id, paused: true);
        Assert.True(pause.Success);
        Assert.Equal(SessionState.SystemPaused, (await harness2.Sessions.FindAsync(session2Id))!.State);

        harness2.Clock.UtcNow = harness2.Clock.UtcNow.AddHours(48);
        var result2 = await Sweep(harness2).SweepAsync();
        Assert.Equal(1, result2.ProfilesScanned);
        Assert.Equal(0, result2.SessionsAbandoned);
        Assert.Equal(SessionState.SystemPaused, (await harness2.Sessions.FindAsync(session2Id))!.State);
    }

    [Fact]
    public async Task No_profile_scans_zero_profiles_and_abandons_nothing()
    {
        var clock = new FakeClock();
        var profiles = new InMemoryLocalProfileRepository();
        var sessions = new InMemoryFocusSessionRepository();
        var ledger = new InMemoryRewardLedgerRepository();
        var events = new InMemorySessionEventRepository();
        var tasks = new InMemoryStudyTaskRepository();
        var rewardState = new FakeRewardStateProvider(sessions, tasks, clock);
        var rewards = new RewardService(
            ledger, new InMemoryAchievementRepository(), new InMemoryUserAchievementRepository(), rewardState, clock);
        var recorder = new SessionEventRecorder(events, clock);
        var recover = new RecoverFocusSessionHandler(
            sessions, profiles, new FakeUnitOfWork(), rewards, clock, recorder);
        var sweep = new PendingRecoverySweep(
            profiles, sessions, new AbandonFocusSessionHandler(sessions, clock, recover), clock);

        var result = await sweep.SweepAsync();

        Assert.Equal(0, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);
        Assert.Empty(ledger.All);
        Assert.Empty(events.All);
    }

    #endregion

    #region Exact 24-Hour Boundary

    [Fact]
    public async Task One_second_before_the_window_keeps_the_session_recoverable()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
        Assert.Equal(interruptedAt, session.InterruptedAtUtc);
        Assert.Null(session.EndedAtUtc);
        Assert.Empty(harness.Ledger.All);
        Assert.DoesNotContain(harness.Events.All, e => e.EventType == SessionEventType.Abandoned);

        // The remaining second is still usable: the user can resume.
        var resumed = await harness.RecoverSession.HandleAsync(sessionId);
        Assert.True(resumed.Success);
        Assert.Equal(SessionState.Active, (await harness.Sessions.FindAsync(sessionId))!.State);
    }

    [Fact]
    public async Task Subsecond_before_the_window_at_999_milliseconds_keeps_the_session_recoverable()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddMilliseconds(-1);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
        Assert.False(session.IsPendingInterruptionExpired(harness.Clock.UtcNow));

        var pending = await harness.PendingSession.HandleAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.False(pending.IsExpired);

        var resumed = await harness.RecoverSession.HandleAsync(sessionId);
        Assert.True(resumed.Success);
        Assert.Equal(SessionState.Active, (await harness.Sessions.FindAsync(sessionId))!.State);
    }

    [Fact]
    public async Task Exactly_at_the_window_abandons_with_unknown_reason_and_one_partial_reward()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(1, result.SessionsAbandoned);
        Assert.Equal(sessionId, Assert.Single(result.AbandonedSessionIds));

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);
        Assert.Equal(interruptedAt, session.InterruptedAtUtc);
        Assert.Equal(interruptedAt.Add(RecoveryLifetime), session.EndedAtUtc);

        var partial = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, partial.SourceType);
        Assert.Equal(15, partial.Amount);
        Assert.StartsWith("SessionPartial:", partial.IdempotencyKey, StringComparison.Ordinal);

        var events = harness.Events.All
            .Where(e => e.SessionType == SessionType.Focus && e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.EventType);
        Assert.Equal(
            new[]
            {
                SessionEventType.Start,
                SessionEventType.Interrupted,
                SessionEventType.Abandoned,
                SessionEventType.RewardCreated,
            },
            events);
    }

    [Fact]
    public async Task Subsecond_after_the_window_at_1_millisecond_abandons()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        var expiryTime = interruptedAt.Add(RecoveryLifetime).AddMilliseconds(1);
        harness.Clock.UtcNow = expiryTime;

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(1, result.SessionsAbandoned);
        Assert.Equal(sessionId, Assert.Single(result.AbandonedSessionIds));

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);
        Assert.Equal(expiryTime, session.EndedAtUtc);
    }

    [Fact]
    public async Task One_second_after_the_window_abandons()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(1);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(1, result.SessionsAbandoned);
        Assert.Equal(sessionId, Assert.Single(result.AbandonedSessionIds));
        Assert.Equal(SessionState.Abandoned, (await harness.Sessions.FindAsync(sessionId))!.State);
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
    }

    [Fact]
    public async Task Legacy_session_without_interrupted_at_utc_uses_updated_at_utc_for_boundary()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);

        MakeLegacySession(session!, interruptedAt);
        Assert.Null(session.InterruptedAtUtc);
        Assert.Equal(interruptedAt, session.EffectiveInterruptionAnchorUtc);

        // 1 second before window based on UpdatedAtUtc
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);
        var beforeResult = await Sweep(harness).SweepAsync();
        Assert.Equal(0, beforeResult.SessionsAbandoned);
        Assert.Equal(SessionState.RecoverableInterrupted, session.State);

        // At exactly 24 hours
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);
        var expiredResult = await Sweep(harness).SweepAsync();
        Assert.Equal(1, expiredResult.SessionsAbandoned);
        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
    }

    #endregion

    #region Clock Anomaly & Time Warp

    [Fact]
    public async Task Future_or_clock_skewed_anchor_is_never_abandoned()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        // The clock is behind the recorded interruption: age is clamped to zero.
        harness.Clock.UtcNow = interruptedAt.AddHours(-2);

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
        Assert.Equal(TimeSpan.Zero, session.PendingInterruptionAge(harness.Clock.UtcNow));
        Assert.Empty(harness.Ledger.All);
        Assert.DoesNotContain(harness.Events.All, e => e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Clock_warp_backward_prior_to_interruption_clamps_age_to_zero_and_keeps_recoverable()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddDays(-14);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(TimeSpan.Zero, session!.PendingInterruptionAge(harness.Clock.UtcNow));
        Assert.False(session.IsPendingInterruptionExpired(harness.Clock.UtcNow));

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Equal(SessionState.RecoverableInterrupted, session.State);
        Assert.Empty(harness.Ledger.All);
    }

    [Fact]
    public async Task Clock_warp_backward_while_pending_retains_recoverability_until_later_advancement()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        // Advances to 20 hours
        harness.Clock.UtcNow = interruptedAt.AddHours(20);
        var sweep1 = await Sweep(harness).SweepAsync();
        Assert.Equal(0, sweep1.SessionsAbandoned);

        // Clock warps backwards to 3 hours after interruption (e.g. system clock sync/NTP fix)
        harness.Clock.UtcNow = interruptedAt.AddHours(3);
        var sweep2 = await Sweep(harness).SweepAsync();
        Assert.Equal(0, sweep2.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
        Assert.Equal(TimeSpan.FromHours(3), session.PendingInterruptionAge(harness.Clock.UtcNow));

        // Clock later advances to 24 hours
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);
        var sweep3 = await Sweep(harness).SweepAsync();
        Assert.Equal(1, sweep3.SessionsAbandoned);
        Assert.Equal(SessionState.Abandoned, (await harness.Sessions.FindAsync(sessionId))!.State);
        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task Clock_warp_backward_after_abandonment_never_resurrects_or_duplicates_rewards()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var initialSweep = await Sweep(harness).SweepAsync();
        Assert.Equal(1, initialSweep.SessionsAbandoned);
        Assert.Single(harness.Ledger.All);
        var initialEventsCount = harness.Events.All.Count;

        // Clock warps backward by 48 hours before interruption
        harness.Clock.UtcNow = interruptedAt.AddHours(-48);
        var backwardSweep = await Sweep(harness).SweepAsync();
        Assert.Equal(0, backwardSweep.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Single(harness.Ledger.All);
        Assert.Equal(initialEventsCount, harness.Events.All.Count);

        // Clock advances to 30 hours after interruption
        harness.Clock.UtcNow = interruptedAt.AddHours(30);
        var forwardSweep = await Sweep(harness).SweepAsync();
        Assert.Equal(0, forwardSweep.SessionsAbandoned);
        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.Single(harness.Ledger.All);
        Assert.Equal(initialEventsCount, harness.Events.All.Count);
    }

    [Fact]
    public async Task Clock_warp_forward_massive_jump_abandons_cleanly_without_time_overflow()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        var futureTime = interruptedAt.AddDays(365);
        harness.Clock.UtcNow = futureTime;

        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(1, result.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);
        Assert.Equal(futureTime, session.EndedAtUtc);
        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task Clock_jitter_oscillating_around_24_hour_boundary_transitions_once_and_stabilizes()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        var sweep = Sweep(harness);

        // 1. T + 23:59:58 -> unexpired
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-2);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        // 2. T + 23:59:59 -> unexpired
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        // 3. Jitter backward: T + 23:59:50 -> unexpired
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-10);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        // 4. Exact boundary: T + 24:00:00 -> expired & abandoned
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);
        var boundaryResult = await sweep.SweepAsync();
        Assert.Equal(1, boundaryResult.SessionsAbandoned);

        // 5. Jitter backward after abandonment: T + 23:59:59 -> stays abandoned, 0 abandoned
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        // 6. Forward again: T + 24:00:05 -> stays abandoned, 0 abandoned
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(5);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
    }

    #endregion

    #region Agent Restart & Offline Execution

    [Fact]
    public async Task Agent_offline_for_extended_period_sweeps_expired_session_on_first_startup_pass()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        // Agent went offline for 72 hours
        harness.Clock.UtcNow = interruptedAt.AddHours(72);

        // Agent boots up and executes its initial background sweep pass
        var startupSweep = Sweep(harness);
        var result = await startupSweep.SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(1, result.SessionsAbandoned);
        Assert.Equal(sessionId, Assert.Single(result.AbandonedSessionIds));

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);
        Assert.Equal(harness.Clock.UtcNow, session.EndedAtUtc);

        var partial = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, partial.SourceType);
        Assert.Equal(15, partial.Amount);
    }

    [Fact]
    public async Task Missed_periodic_sweep_executions_catch_up_cleanly_without_duplicate_abandonment()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        var sweep = Sweep(harness);

        // Sweeps ran normally until hour 23
        harness.Clock.UtcNow = interruptedAt.AddHours(23);
        Assert.Equal(0, (await sweep.SweepAsync()).SessionsAbandoned);

        // Agent was offline from hour 23 to hour 30 (missed many 15-minute sweep ticks)
        // Resume periodic ticks at hour 30, hour 30:15, hour 30:30
        harness.Clock.UtcNow = interruptedAt.AddHours(30);
        var tick1 = await sweep.SweepAsync();
        Assert.Equal(1, tick1.SessionsAbandoned);

        harness.Clock.UtcNow = interruptedAt.AddHours(30).AddMinutes(15);
        var tick2 = await sweep.SweepAsync();
        Assert.Equal(0, tick2.SessionsAbandoned);

        harness.Clock.UtcNow = interruptedAt.AddHours(30).AddMinutes(30);
        var tick3 = await sweep.SweepAsync();
        Assert.Equal(0, tick3.SessionsAbandoned);

        Assert.Single(harness.Ledger.All);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Agent_restart_with_fresh_harness_sweeps_persisted_interrupted_session()
    {
        var clock = new FakeClock();
        var profiles = new InMemoryLocalProfileRepository();
        var sessions = new InMemoryFocusSessionRepository();
        var ledger = new InMemoryRewardLedgerRepository();
        var events = new InMemorySessionEventRepository();
        var tasks = new InMemoryStudyTaskRepository();
        var achievements = new InMemoryAchievementRepository();
        var userAchievements = new InMemoryUserAchievementRepository();
        var uow = new FakeUnitOfWork();

        var profile = LocalProfile.Create(clock.UtcNow, "Test profile");
        await profiles.AddAsync(profile);

        // Process 1: Starts session, checkpoints, interrupts, then crashes
        var session = FocusSession.Start(
            profile.Id,
            BuiltInProfiles.BalancedCode,
            25 * 60,
            clock.UtcNow,
            EconomyRules.Version);
        await sessions.AddAsync(session);

        clock.UtcNow = clock.UtcNow.AddMinutes(ActiveMinutes);
        session.ApplyCheckpoint(clock.UtcNow, ActiveMinutes * 60, ActiveMinutes * 60, 0, 0, 0, 0, 0);

        var interruptedAt = clock.UtcNow;
        session.MarkRecoverableInterrupted(interruptedAt, FocusEndReason.AgentCrash);

        // Process 1 dies.
        // Process 2 starts up 26 hours later sharing the persisted stores
        clock.UtcNow = interruptedAt.AddHours(26);

        var rewardState2 = new FakeRewardStateProvider(sessions, tasks, clock);
        var rewards2 = new RewardService(ledger, achievements, userAchievements, rewardState2, clock);
        var recorder2 = new SessionEventRecorder(events, clock);
        var recover2 = new RecoverFocusSessionHandler(sessions, profiles, uow, rewards2, clock, recorder2);
        var abandon2 = new AbandonFocusSessionHandler(sessions, clock, recover2);
        var sweep2 = new PendingRecoverySweep(profiles, sessions, abandon2, clock);

        var sweepResult = await sweep2.SweepAsync();

        Assert.Equal(1, sweepResult.ProfilesScanned);
        Assert.Equal(1, sweepResult.SessionsAbandoned);
        Assert.Equal(session.Id, Assert.Single(sweepResult.AbandonedSessionIds));

        var reloaded = await sessions.FindAsync(session.Id);
        Assert.Equal(SessionState.Abandoned, reloaded!.State);
        Assert.Equal(FocusEndReason.Unknown, reloaded.EndReason);
        Assert.Single(ledger.All);
    }

    #endregion

    #region Race Conditions & Concurrency

    [Fact]
    public async Task Repeated_sweep_abandons_nothing_more_and_duplicates_no_reward_or_event()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var first = await Sweep(harness).SweepAsync();
        Assert.Equal(1, first.SessionsAbandoned);

        var ledgerCount = harness.Ledger.All.Count;
        var eventCount = harness.Events.All.Count;
        var sessionEventCount = harness.Events.All.Count(e => e.SessionId == sessionId);

        var second = await Sweep(harness).SweepAsync();

        Assert.Equal(1, second.ProfilesScanned);
        Assert.Equal(0, second.SessionsAbandoned);
        Assert.Empty(second.AbandonedSessionIds);
        Assert.Equal(ledgerCount, harness.Ledger.All.Count);
        Assert.Equal(eventCount, harness.Events.All.Count);
        Assert.Equal(sessionEventCount, harness.Events.All.Count(e => e.SessionId == sessionId));
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e =>
            e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
        Assert.Single(harness.Events.All, e =>
            e.SessionId == sessionId && e.EventType == SessionEventType.RewardCreated);
        Assert.Equal(SessionState.Abandoned, (await harness.Sessions.FindAsync(sessionId))!.State);
    }

    [Fact]
    public async Task Sweep_after_resume_does_not_abandon_or_duplicate_a_reward()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        harness.Clock.UtcNow = interruptedAt.AddHours(1);
        var resumed = await harness.RecoverSession.HandleAsync(sessionId);
        Assert.True(resumed.Success);
        Assert.True(resumed.Changed);
        Assert.Equal(SessionState.Active, (await harness.Sessions.FindAsync(sessionId))!.State);

        // Long past the window the session is Active, so the sweep must ignore it.
        harness.Clock.UtcNow = interruptedAt.AddHours(25);
        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Null(session.EndedAtUtc);
        Assert.Empty(harness.Ledger.All);
        Assert.DoesNotContain(harness.Events.All, e => e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Race_user_resumes_after_sweep_abandons_returns_idempotent_result_and_does_not_reactivate()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // Background sweep runs first and abandons
        var sweepResult = await Sweep(harness).SweepAsync();
        Assert.Equal(1, sweepResult.SessionsAbandoned);
        Assert.Single(harness.Ledger.All);

        // User concurrently or subsequently attempts to resume the session
        var resumeResult = await harness.RecoverSession.HandleAsync(sessionId);

        Assert.True(resumeResult.Success);
        Assert.False(resumeResult.Changed); // Idempotent existing outcome
        Assert.Equal(15, resumeResult.XpAwarded);

        // Crucial verification: session must stay Abandoned and NOT revert to Active!
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);
        Assert.Equal(interruptedAt.Add(RecoveryLifetime), session.EndedAtUtc);

        // Ledger must have exactly one partial award
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Race_user_resumes_at_boundary_which_auto_abandons_then_sweep_runs_as_noop()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // User calls RecoverSession at 24:00:00, which auto-abandons due to 24h expiration
        var resumeResult = await harness.RecoverSession.HandleAsync(sessionId);
        Assert.True(resumeResult.Success);
        Assert.True(resumeResult.Changed);
        Assert.Equal(15, resumeResult.XpAwarded);

        var sessionAfterResume = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, sessionAfterResume!.State);

        // Background sweep runs immediately after
        var sweepResult = await Sweep(harness).SweepAsync();
        Assert.Equal(1, sweepResult.ProfilesScanned);
        Assert.Equal(0, sweepResult.SessionsAbandoned);
        Assert.Empty(sweepResult.AbandonedSessionIds);

        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Race_sweep_runs_after_user_explicitly_discards_preserves_user_end_reason()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        // User explicitly discards at hour 2 with reason UserEnded
        harness.Clock.UtcNow = interruptedAt.AddHours(2);
        var discard = await harness.Abandon.HandleAsync(sessionId, FocusEndReason.UserEnded);
        Assert.True(discard.Success);
        Assert.True(discard.Changed);
        Assert.Equal(15, discard.XpAwarded);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.UserEnded, session.EndReason);

        // Background sweep runs at hour 25
        harness.Clock.UtcNow = interruptedAt.AddHours(25);
        var sweepResult = await Sweep(harness).SweepAsync();

        Assert.Equal(1, sweepResult.ProfilesScanned);
        Assert.Equal(0, sweepResult.SessionsAbandoned);
        Assert.Empty(sweepResult.AbandonedSessionIds);

        // EndReason remains UserEnded, not overwritten by Unknown!
        var finalSession = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(FocusEndReason.UserEnded, finalSession!.EndReason);
        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task Race_user_discards_after_sweep_abandons_returns_idempotent_outcome()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // Sweep runs first
        var sweepResult = await Sweep(harness).SweepAsync();
        Assert.Equal(1, sweepResult.SessionsAbandoned);

        // User clicks Discard with UserEnded right after sweep
        var discard = await harness.Abandon.HandleAsync(sessionId, FocusEndReason.UserEnded);
        Assert.True(discard.Success);
        Assert.False(discard.Changed); // Already terminal

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason); // Kept original from sweep
        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task Sweep_after_a_new_session_auto_abandoned_the_old_one_makes_no_second_transition()
    {
        var (harness, oldSessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var started = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.True(started.Success, started.Error?.Message);
        var newSessionId = started.Id!.Value;

        var ledgerCount = harness.Ledger.All.Count;
        var oldSessionEventCount = harness.Events.All.Count(e => e.SessionId == oldSessionId);
        Assert.Equal(1, ledgerCount);
        Assert.Equal(4, oldSessionEventCount);

        harness.Clock.UtcNow = interruptedAt.AddHours(30);
        var result = await Sweep(harness).SweepAsync();

        Assert.Equal(1, result.ProfilesScanned);
        Assert.Equal(0, result.SessionsAbandoned);
        Assert.Empty(result.AbandonedSessionIds);

        var oldSession = await harness.Sessions.FindAsync(oldSessionId);
        Assert.Equal(SessionState.Abandoned, oldSession!.State);
        Assert.Equal(FocusEndReason.Unknown, oldSession.EndReason);
        Assert.Equal(interruptedAt.Add(RecoveryLifetime), oldSession.EndedAtUtc);

        var newSession = await harness.Sessions.FindAsync(newSessionId);
        Assert.Equal(SessionState.Active, newSession!.State);

        Assert.Equal(ledgerCount, harness.Ledger.All.Count);
        Assert.Equal(oldSessionEventCount, harness.Events.All.Count(e => e.SessionId == oldSessionId));
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e =>
            e.SessionId == oldSessionId && e.EventType == SessionEventType.Abandoned);
    }

    [Fact]
    public async Task Race_user_starts_new_session_after_sweep_abandons_succeeds_without_blocking()
    {
        var (harness, oldSessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // Sweep abandons old session first
        var sweepResult = await Sweep(harness).SweepAsync();
        Assert.Equal(1, sweepResult.SessionsAbandoned);

        // User immediately starts new session
        var startResult = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.True(startResult.Success, startResult.Error?.Message);
        var newSessionId = startResult.Id!.Value;

        var oldSession = await harness.Sessions.FindAsync(oldSessionId);
        Assert.Equal(SessionState.Abandoned, oldSession!.State);

        var newSession = await harness.Sessions.FindAsync(newSessionId);
        Assert.Equal(SessionState.Active, newSession!.State);

        // Only 1 ledger entry exists (for the abandoned session)
        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task Simulated_concurrent_sweep_passes_abandon_session_exactly_once()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var sweep = Sweep(harness);

        // Simulate 4 concurrent sweep calls
        var results = await Task.WhenAll(
            sweep.SweepAsync(),
            sweep.SweepAsync(),
            sweep.SweepAsync(),
            sweep.SweepAsync());

        var totalAbandoned = results.Sum(r => r.SessionsAbandoned);
        Assert.Equal(1, totalAbandoned);
        Assert.Single(results.SelectMany(r => r.AbandonedSessionIds));

        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.Abandoned);
        Assert.Single(harness.Events.All, e => e.SessionId == sessionId && e.EventType == SessionEventType.RewardCreated);
    }

    #endregion

    #region Partial Rewards & Event Integrity

    [Fact]
    public async Task Expired_session_calculates_exact_partial_reward_for_strict_profile()
    {
        // Strict profile: Rate is 4 XP/min. 20 active minutes -> raw = 20 * 4 * 0.5 = 40 XP.
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.StrictCode,
            plannedMinutes: 30,
            activeMinutes: 20);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var partial = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, partial.SourceType);
        Assert.Equal(40, partial.Amount);
        var expectedKey = IdempotencyKey.For(LedgerSourceType.SessionPartial, sessionId.ToString(), CurrencyCode.Xp).Value;
        Assert.Equal(expectedKey, partial.IdempotencyKey);
    }

    [Fact]
    public async Task Expired_session_calculates_exact_partial_reward_for_flexible_profile()
    {
        // Flexible profile: Rate is 2 XP/min. 15 active minutes -> raw = 15 * 2 * 0.5 = 15 XP.
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.FlexibleCode,
            plannedMinutes: 25,
            activeMinutes: 15);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var partial = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, partial.SourceType);
        Assert.Equal(15, partial.Amount);
    }

    [Fact]
    public async Task Expired_session_with_under_10_minutes_active_study_awards_zero_xp_and_creates_no_ledger_entry()
    {
        // 5 active minutes (< 10 min threshold): ComputeSessionXp returns 0 XP
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.BalancedCode,
            plannedMinutes: 25,
            activeMinutes: 5);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);

        // Cap-zero / threshold-zero sessions produce NO ledger entry
        Assert.Empty(harness.Ledger.All);

        // Events must have Start, Interrupted, Abandoned, but NO RewardCreated
        var events = harness.Events.All
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.EventType)
            .ToArray();
        Assert.Equal(
            new[] { SessionEventType.Start, SessionEventType.Interrupted, SessionEventType.Abandoned },
            events);
    }

    [Fact]
    public async Task Expired_session_with_zero_active_study_seconds_creates_no_ledger_entry()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.BalancedCode,
            plannedMinutes: 25,
            activeMinutes: 0);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Empty(harness.Ledger.All);

        var events = harness.Events.All
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .Select(e => e.EventType)
            .ToArray();
        Assert.Equal(
            new[] { SessionEventType.Start, SessionEventType.Interrupted, SessionEventType.Abandoned },
            events);
    }

    [Fact]
    public async Task Expired_session_respects_daily_session_xp_cap_and_clamps_award()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.BalancedCode,
            plannedMinutes: 25,
            activeMinutes: 20); // 20 min on Balanced = 30 XP

        // Advance to 24 hours (expiry time) so that SeedPriorSessionXp targets the abandonment date
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // Seed 290 XP prior on today's local date (cap is 300)
        harness.SeedPriorSessionXp(290, "prior-session");

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        // Total award should be clamped to 300 - 290 = 10 XP
        var partialEntry = harness.Ledger.All.Single(e => e.SourceType == LedgerSourceType.SessionPartial);
        Assert.Equal(10, partialEntry.Amount);

        var totalToday = await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today);
        Assert.Equal(EconomyRules.DailySessionXpCap, totalToday);
    }

    [Fact]
    public async Task Expired_session_when_daily_cap_already_exhausted_awards_zero_xp_and_no_ledger_entry()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.BalancedCode,
            plannedMinutes: 25,
            activeMinutes: 20);

        // Advance to 24 hours (expiry time) so that SeedPriorSessionXp targets the abandonment date
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // Seed 300 XP prior for today (daily cap reached on the day of abandonment)
        harness.SeedPriorSessionXp(300, "prior-session-max");

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        // No new ledger entry should be created since award is 0 XP
        Assert.DoesNotContain(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);
    }

    [Fact]
    public async Task Expired_session_is_never_streak_qualified()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession(
            profileCode: BuiltInProfiles.BalancedCode,
            plannedMinutes: 30,
            activeMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, session!.State);

        // Abandoned sessions are never streak-qualified, regardless of active minutes
        Assert.False(EconomyRules.IsStreakQualified(session.State, session.ActiveStudySeconds));

        var rewardState = new FakeRewardStateProvider(harness.Sessions, harness.Tasks, harness.Clock);
        var qualifiedDates = await rewardState.ListQualifiedLocalDatesAsync(harness.Profile.Id);
        Assert.Empty(qualifiedDates);
    }

    [Fact]
    public async Task Expired_session_records_strictly_monotonic_event_sequence_with_exact_types()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await Sweep(harness).SweepAsync();
        Assert.Equal(1, result.SessionsAbandoned);

        var events = harness.Events.All
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToList();

        Assert.Equal(4, events.Count);

        Assert.Equal(1, events[0].Sequence);
        Assert.Equal(SessionEventType.Start, events[0].EventType);

        Assert.Equal(2, events[1].Sequence);
        Assert.Equal(SessionEventType.Interrupted, events[1].EventType);

        Assert.Equal(3, events[2].Sequence);
        Assert.Equal(SessionEventType.Abandoned, events[2].EventType);

        Assert.Equal(4, events[3].Sequence);
        Assert.Equal(SessionEventType.RewardCreated, events[3].EventType);
    }

    #endregion

    private static PendingRecoverySweep Sweep(TestHarness harness) =>
        new(harness.Profiles, harness.Sessions, harness.Abandon, harness.Clock);

    private static (TestHarness Harness, Guid SessionId, DateTime InterruptedAt) InterruptedSession(
        string profileCode = "Balanced",
        int plannedMinutes = 25,
        int activeMinutes = ActiveMinutes,
        TestHarness? existingHarness = null)
    {
        var harness = existingHarness ?? new TestHarness();
        var sessionId = harness.StartSessionAndGetId(profileCode, plannedMinutes: plannedMinutes);
        if (activeMinutes > 0)
        {
            harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(activeMinutes);
            var checkpoint = harness.Checkpoint.HandleAsync(
                sessionId, activeMinutes * 60, activeMinutes * 60, 0, 0, 0, 0, 0).GetAwaiter().GetResult();
            Assert.True(checkpoint.Success, checkpoint.Error?.Message);
        }
        else
        {
            harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(10);
            var checkpoint = harness.Checkpoint.HandleAsync(
                sessionId, 10, 0, 10, 0, 0, 0, 0).GetAwaiter().GetResult();
            Assert.True(checkpoint.Success, checkpoint.Error?.Message);
        }

        var interruptedAt = harness.Clock.UtcNow;
        var interrupt = harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash).GetAwaiter().GetResult();
        Assert.True(interrupt.Success, interrupt.Error?.Message);
        return (harness, sessionId, interruptedAt);
    }

    private static void MakeLegacySession(FocusSession session, DateTime anchor)
    {
        typeof(FocusSession).GetProperty(nameof(FocusSession.InterruptedAtUtc))!
            .SetValue(session, null);
        typeof(FocusSession).GetProperty(nameof(FocusSession.UpdatedAtUtc))!
            .SetValue(session, anchor);
    }
}