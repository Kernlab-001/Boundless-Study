using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

/// <summary>
/// The approved focus-session state machine (domain-business-rules.md §5,
/// conceptual-data-model.md §4.5 invariants).
/// </summary>
public class FocusSessionTests
{
    private static FocusSession NewSession(int plannedMinutes = 25) => FocusSession.Start(
        Guid.NewGuid(),
        "Balanced",
        plannedMinutes * 60,
        new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc),
        economyRuleVersion: "ECONOMY-1");

    [Fact]
    public void Start_creates_an_active_session_with_snapshots()
    {
        var session = FocusSession.Start(
            Guid.NewGuid(),
            "Strict",
            30 * 60,
            new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc),
            "ECONOMY-1",
            taskId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            taskTitleSnapshot: "Read chapter 4",
            subjectNameSnapshot: "History");

        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal(AppRules.Version, session.AppRuleVersion);
        Assert.Equal("APP-RULES-1", session.AppRuleVersion);
        Assert.Equal("Read chapter 4", session.TaskTitleSnapshot);
        Assert.Equal("History", session.SubjectNameSnapshot);
        Assert.Equal(0, session.ElapsedSeconds);
        Assert.Equal(0, session.ActiveStudySeconds);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(10_801)]
    [InlineData(1499)] // 25 min is valid; 24.9 min is not (5-minute increments)
    public void Planned_duration_must_be_300_to_10800_in_5_minute_steps(int plannedSeconds)
    {
        Assert.Throws<DomainException>(() => FocusSession.Start(
            Guid.NewGuid(), "Balanced", plannedSeconds,
            DateTime.UtcNow, "ECONOMY-1"));
    }

    [Fact]
    public void Active_to_blocked_and_back()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        session.Block(at);
        Assert.Equal(SessionState.Blocked, session.State);

        session.Unblock(at);
        Assert.Equal(SessionState.Active, session.State);
    }

    [Fact]
    public void Active_to_system_paused_and_back()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        session.SystemPause(at);
        Assert.Equal(SessionState.SystemPaused, session.State);

        session.SystemResume(at);
        Assert.Equal(SessionState.Active, session.State);
    }

    [Fact]
    public void Completion_requires_elapsed_at_or_above_planned()
    {
        var session = NewSession(plannedMinutes: 25);
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.Complete(at, elapsedSeconds: 24 * 60, activeStudySeconds: 24 * 60, idleSeconds: 0, protectionGapSeconds: 0));

        session.Complete(at, elapsedSeconds: 25 * 60, activeStudySeconds: 25 * 60, idleSeconds: 0, protectionGapSeconds: 0);
        Assert.Equal(SessionState.Completed, session.State);
        Assert.Equal(FocusEndReason.Completed, session.EndReason);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void Ended_early_requires_a_non_completed_reason()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.EndEarly(at, 5 * 60, 5 * 60, 0, 0, FocusEndReason.Completed));

        session.EndEarly(at, 5 * 60, 5 * 60, 0, 0, FocusEndReason.UserEnded);
        Assert.Equal(SessionState.EndedEarly, session.State);
        Assert.Equal(FocusEndReason.UserEnded, session.EndReason);
    }

    [Fact]
    public void Active_blocked_and_paused_all_become_recoverable_interrupted()
    {
        foreach (var setup in new Func<FocusSession, DateTime, FocusSession>[]
                 {
                     (s, at) => { s.Block(at); return s; },
                     (s, at) => { s.SystemPause(at); return s; },
                     (s, at) => s,
                 })
        {
            var session = NewSession();
            var at = DateTime.UtcNow;
            setup(session, at);

            session.MarkRecoverableInterrupted(at, FocusEndReason.AgentCrash);
            Assert.Equal(SessionState.RecoverableInterrupted, session.State);
        }
    }

    [Fact]
    public void Interruption_requires_an_involuntary_reason()
    {
        var session = NewSession();
        Assert.Throws<DomainException>(() => session.MarkRecoverableInterrupted(DateTime.UtcNow, FocusEndReason.Completed));
        Assert.Throws<DomainException>(() => session.MarkRecoverableInterrupted(DateTime.UtcNow, FocusEndReason.UserEnded));
    }

    [Fact]
    public void Recovery_returns_to_active_and_counts()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;
        session.MarkRecoverableInterrupted(at, FocusEndReason.Shutdown);

        session.Recover(at);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Null(session.EndReason);
        Assert.Equal(1, session.RecoveryCount);

        // Terminal sessions cannot be recovered.
        session.Complete(at, 25 * 60, 25 * 60, 0, 0);
        Assert.Throws<DomainException>(() => session.Recover(at));
    }

    [Fact]
    public void Recovery_completion_requires_checkpoint_at_or_above_planned()
    {
        var at = DateTime.UtcNow;
        var planned = 25 * 60;

        // Checkpoint below planned: finalization is rejected.
        var below = NewSession(plannedMinutes: 25);
        below.ApplyCheckpoint(at, elapsedSeconds: planned - 60, planned - 60, 0, 0, 0, 0, 0);
        below.MarkRecoverableInterrupted(at, FocusEndReason.PowerLoss);
        Assert.Throws<DomainException>(() => below.CompleteFromCheckpoint(at));

        // TV-06: checkpoint already met the threshold → completes from the checkpoint, once.
        var atThreshold = NewSession(plannedMinutes: 25);
        atThreshold.ApplyCheckpoint(at, elapsedSeconds: planned, planned, 0, 0, 0, 0, 0);
        atThreshold.MarkRecoverableInterrupted(at, FocusEndReason.PowerLoss);
        atThreshold.CompleteFromCheckpoint(at);

        Assert.Equal(SessionState.Completed, atThreshold.State);
        Assert.Equal(planned, atThreshold.ElapsedSeconds);
        // Downtime is never invented: elapsed stays at the checkpoint value.
        Assert.Equal(planned, atThreshold.ActiveStudySeconds);
    }

    [Fact]
    public void Abandon_is_allowed_only_from_recoverable_interrupted()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.Abandon(at, FocusEndReason.Unknown));

        session.MarkRecoverableInterrupted(at, FocusEndReason.Shutdown);
        session.Abandon(at, FocusEndReason.Unknown);
        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.True(session.IsTerminal);
    }

    [Fact]
    public void Terminal_states_cannot_move_again()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;
        session.Complete(at, 25 * 60, 25 * 60, 0, 0);

        Assert.Throws<DomainException>(() => session.Block(at));
        Assert.Throws<DomainException>(() => session.EndEarly(at, 25 * 60, 25 * 60, 0, 0, FocusEndReason.UserEnded));
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(at, 26 * 60, 25 * 60, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Checkpoints_update_counters_and_require_a_live_session()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        session.ApplyCheckpoint(at, elapsedSeconds: 10 * 60, activeStudySeconds: 9 * 60, idleSeconds: 60, protectionGapSeconds: 5, violationCount: 1, exceptionCount: 1, recoveryCount: 0);

        Assert.Equal(10 * 60, session.ElapsedSeconds);
        Assert.Equal(9 * 60, session.ActiveStudySeconds);
        Assert.Equal(60, session.IdleSeconds);
        Assert.Equal(5, session.ProtectionGapSeconds);
        Assert.Equal(1, session.ViolationCount);
        Assert.Equal(1, session.ExceptionCount);
        Assert.NotNull(session.LastCheckpointAtUtc);
        Assert.Equal(10 * 60, session.LastCheckpointElapsedSeconds);
    }

    [Fact]
    public void Active_study_cannot_exceed_elapsed()
    {
        var session = NewSession();
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(
            DateTime.UtcNow, elapsedSeconds: 100, activeStudySeconds: 101, idleSeconds: 0, protectionGapSeconds: 0, violationCount: 0, exceptionCount: 0, recoveryCount: 0));
        Assert.Throws<DomainException>(() => session.Complete(
            DateTime.UtcNow, elapsedSeconds: 25 * 60, activeStudySeconds: 25 * 60 + 1, idleSeconds: 0, protectionGapSeconds: 0));
    }

    [Fact]
    public void Counters_cannot_be_negative()
    {
        var session = NewSession();
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(
            DateTime.UtcNow, elapsedSeconds: -1, activeStudySeconds: 0, idleSeconds: 0, protectionGapSeconds: 0, violationCount: 0, exceptionCount: 0, recoveryCount: 0));
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(
            DateTime.UtcNow, 0, 0, 0, 0, violationCount: -1, exceptionCount: 0, recoveryCount: 0));
    }

    [Fact]
    public void Wrong_state_transitions_are_rejected()
    {
        var session = NewSession();
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.Unblock(at));           // not Blocked
        Assert.Throws<DomainException>(() => session.SystemResume(at));      // not SystemPaused

        session.SystemPause(at);
        Assert.Throws<DomainException>(() => session.Block(at));             // SystemPaused → Blocked is not approved
        Assert.Throws<DomainException>(() => session.Complete(at, 25 * 60, 25 * 60, 0, 0)); // must return to Active first
    }

    [Fact]
    public void Protection_status_can_be_recorded()
    {
        var session = NewSession();
        session.SetProtection(ProtectionStatus.Degraded, VerificationStatus.Degraded, DateTime.UtcNow);
        Assert.Equal(ProtectionStatus.Degraded, session.ProtectionStatus);
        Assert.Equal(VerificationStatus.Degraded, session.VerificationStatus);
    }
}
