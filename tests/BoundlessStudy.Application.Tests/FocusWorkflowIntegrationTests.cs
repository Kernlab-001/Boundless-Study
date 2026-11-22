using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Permanent integration tests for the Focus Session lifecycle, history query,
/// and recovery blocking workflows (Milestone 4, SCR-001, SCR-016).
/// Verifies:
/// 1. Starting a focus session with valid profile and duration.
/// 2. Blocking a new focus session while an unexpired recoverable session exists.
/// 3. Completing a focus session and verifying XP reward, level calculation, and streak qualification.
/// 4. Ending a focus session early and verifying partial XP calculation (0.5 multiplier) and non-qualification.
/// 5. Session history query and accurate summary mapping from sessions and reward ledger.
/// </summary>
public class FocusWorkflowIntegrationTests
{
    [Fact]
    public async Task StartFocusSession_WithValidProfileAndDuration_SucceedsAndCreatesActiveSession()
    {
        var harness = new TestHarness();
        var plannedMinutes = 25;
        var plannedSeconds = plannedMinutes * 60;

        var result = await harness.StartSession.HandleAsync(
            harness.Profile.Id,
            BuiltInProfiles.BalancedCode,
            plannedSeconds);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Id);

        var session = await harness.Sessions.FindAsync(result.Id.Value);
        Assert.NotNull(session);
        Assert.Equal(harness.Profile.Id, session.LocalProfileId);
        Assert.Equal(BuiltInProfiles.BalancedCode, session.ProfileCode);
        Assert.Equal(plannedSeconds, session.PlannedFocusSeconds);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal(0, session.ElapsedSeconds);
        Assert.Equal(0, session.ActiveStudySeconds);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public async Task StartFocusSession_WhenUnexpiredRecoverableSessionExists_BlocksNewSession()
    {
        var harness = new TestHarness();
        var startResult = await harness.StartSession.HandleAsync(
            harness.Profile.Id,
            BuiltInProfiles.BalancedCode,
            25 * 60);
        Assert.True(startResult.Success);
        var sessionId = startResult.Id!.Value;

        // Advance 10 minutes and checkpoint
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(10);
        await harness.Checkpoint.HandleAsync(sessionId, 600, 600, 0, 0, 0, 0, 0);

        // Simulate crash / interruption
        var interruptResult = await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash);
        Assert.True(interruptResult.Success);

        // Verify unexpired session exists
        var pending = await harness.PendingSession.HandleAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.False(pending.IsExpired);

        // Verify recovery coordinator reports starting is disallowed
        var canStart = await harness.RecoveryCoordinator.CanStartNewSessionAsync(harness.Profile.Id);
        Assert.False(canStart, "New session start must be blocked while an unexpired recoverable session is pending.");

        // Attempt to start a new focus session -> must fail with RecoverableSessionPending
        var secondStart = await harness.StartSession.HandleAsync(
            harness.Profile.Id,
            BuiltInProfiles.StrictCode,
            45 * 60);

        Assert.False(secondStart.Success);
        Assert.NotNull(secondStart.Error);
        Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, secondStart.Error.Code);
    }

    [Fact]
    public async Task CompleteFocusSession_AwardsXp_CalculatesLevel_AndQualifiesStreak()
    {
        var harness = new TestHarness();
        var plannedMinutes = 25;
        var plannedSeconds = plannedMinutes * 60;
        var startResult = await harness.StartSession.HandleAsync(
            harness.Profile.Id,
            BuiltInProfiles.BalancedCode,
            plannedSeconds);
        Assert.True(startResult.Success);
        var sessionId = startResult.Id!.Value;

        // Advance clock by full planned duration (25 minutes)
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(plannedMinutes);

        // Complete session
        var outcome = await harness.CompleteSession.HandleAsync(
            sessionId,
            plannedSeconds,
            plannedSeconds,
            idleSeconds: 0,
            protectionGapSeconds: 0);

        Assert.True(outcome.Success);
        Assert.True(outcome.Changed);
        // Balanced profile = 3 XP / active minute -> 25 * 3 = 75 session XP
        Assert.Equal(75, outcome.XpAwarded);
        // First completed session also unlocks ACH-001 (+50 XP), total = 125 XP
        Assert.Equal(125, outcome.TotalXpAfter);
        Assert.True(outcome.IsStreakQualified, "25 active minutes exceeds 20-minute threshold; must be streak qualified.");
        Assert.Contains("ACH-001", outcome.AchievementsUnlocked);

        // Verify profile progress snapshot derives identical values
        var progress = await harness.GetProgress.HandleAsync(harness.Profile.Id);
        Assert.NotNull(progress);
        Assert.Equal(125, progress.TotalXp);
        Assert.Equal(1, progress.Level);
        Assert.Equal(1, progress.CurrentStreakDays);
        Assert.Equal(1, progress.QualifiedSessionCount);
        Assert.Equal(75, progress.SessionXpToday);
        Assert.Equal(EconomyRules.DailySessionXpCap - 75, progress.SessionCapRemainingToday);

        // Verify session entity reached completed state
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Completed, session.State);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public async Task EndFocusEarly_CalculatesPartialXp_AndDoesNotQualifyStreak()
    {
        var harness = new TestHarness();
        var plannedMinutes = 30;
        var plannedSeconds = plannedMinutes * 60;
        // Use Flexible profile (2 XP/min)
        var startResult = await harness.StartSession.HandleAsync(
            harness.Profile.Id,
            BuiltInProfiles.FlexibleCode,
            plannedSeconds);
        Assert.True(startResult.Success);
        var sessionId = startResult.Id!.Value;

        // User studies for 10 minutes then ends early
        var activeMinutes = 10;
        var activeSeconds = activeMinutes * 60;
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(activeMinutes);

        var outcome = await harness.EndEarly.HandleAsync(
            sessionId,
            activeSeconds,
            activeSeconds,
            idleSeconds: 0,
            protectionGapSeconds: 0,
            endReason: FocusEndReason.UserEnded);

        Assert.True(outcome.Success);

        // Flexible rate: 2 XP/min * 10 mins = 20 XP raw.
        // Early end multiplier is 0.5 -> 20 * 0.5 = 10 XP.
        Assert.Equal(10, outcome.XpAwarded);
        Assert.Equal(10, outcome.TotalXpAfter);
        Assert.False(outcome.IsStreakQualified, "Ended early sessions never qualify for streaks.");

        // Check session entity state
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.EndedEarly, session.State);
        Assert.NotNull(session.EndedAtUtc);

        // Progress check
        var progress = await harness.GetProgress.HandleAsync(harness.Profile.Id);
        Assert.NotNull(progress);
        Assert.Equal(10, progress.TotalXp);
        Assert.Equal(0, progress.CurrentStreakDays); // Not qualified
    }

    [Fact]
    public async Task ListSessionHistory_ReturnsSessionsWithCorrectSummaryMapping()
    {
        var harness = new TestHarness();

        // 1. Completed session (25 minutes, Balanced profile (3 XP/min) -> 75 XP)
        var s1Result = await harness.StartSession.HandleAsync(harness.Profile.Id, BuiltInProfiles.BalancedCode, 25 * 60);
        Assert.True(s1Result.Success);
        var s1Id = s1Result.Id!.Value;
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(25);
        await harness.CompleteSession.HandleAsync(s1Id, 1500, 1500, 0, 0);

        // Advance 1 hour
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddHours(1);

        // 2. Early ended session (10 minutes, Strict profile (4 XP/min) -> 10m * 4 XP/min * 0.5 = 20 XP)
        var s2Result = await harness.StartSession.HandleAsync(harness.Profile.Id, BuiltInProfiles.StrictCode, 45 * 60);
        Assert.True(s2Result.Success);
        var s2Id = s2Result.Id!.Value;
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(10);
        await harness.EndEarly.HandleAsync(s2Id, 600, 600, 0, 0, FocusEndReason.UserEnded);

        // 3. Query history
        var historyHandler = new ListFocusSessionHistoryHandler(harness.Profiles, harness.Sessions, harness.Ledger);
        var history = await historyHandler.HandleAsync(harness.Profile.Id);

        Assert.Equal(2, history.Count);

        // First item is most recent (s2)
        var recent = history[0];
        Assert.Equal(s2Id, recent.Id);
        Assert.Equal(BuiltInProfiles.StrictCode, recent.ProfileCode);
        Assert.Equal(SessionState.EndedEarly, recent.State);
        Assert.Equal(600, recent.ElapsedSeconds);
        Assert.Equal(600, recent.ActiveStudySeconds);
        Assert.Equal(20, recent.XpEarned);

        // Second item is earlier (s1)
        var earlier = history[1];
        Assert.Equal(s1Id, earlier.Id);
        Assert.Equal(BuiltInProfiles.BalancedCode, earlier.ProfileCode);
        Assert.Equal(SessionState.Completed, earlier.State);
        Assert.Equal(1500, earlier.ElapsedSeconds);
        Assert.Equal(1500, earlier.ActiveStudySeconds);
        Assert.Equal(75, earlier.XpEarned);
    }
}
