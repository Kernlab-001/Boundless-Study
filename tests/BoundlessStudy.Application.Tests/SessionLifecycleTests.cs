using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Session lifecycle, start preconditions, breaks, exceptions, blocking rules, and
/// the derived progress query (domain-business-rules.md §5–§7, §9).
/// </summary>
public class SessionLifecycleTests
{
    [Fact]
    public async Task Starting_a_second_focus_session_is_rejected()
    {
        var harness = new TestHarness();
        harness.StartSessionAndGetId();

        var second = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.False(second.Success);
        Assert.Equal(UseCaseErrorCodes.ActiveFocusSessionExists, second.Error!.Code);
    }

    [Fact]
    public async Task Starting_a_session_while_a_break_is_active_is_rejected()
    {
        var harness = new TestHarness();
        var breakResult = await harness.StartBreak.HandleAsync(harness.Profile.Id, 5 * 60);
        Assert.True(breakResult.Success);

        var start = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.False(start.Success);
        Assert.Equal(UseCaseErrorCodes.ActiveBreakSessionExists, start.Error!.Code);
    }

    [Fact]
    public async Task A_pending_recoverable_session_must_be_resolved_before_a_new_session()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.Shutdown);

        var start = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.False(start.Success);
        Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, start.Error!.Code);
    }

    [Fact]
    public async Task Unknown_profile_and_invalid_duration_are_rejected()
    {
        var harness = new TestHarness();

        var unknown = await harness.StartSession.HandleAsync(harness.Profile.Id, "Turbo", 25 * 60);
        Assert.False(unknown.Success);
        Assert.Equal(UseCaseErrorCodes.FocusProfileNotFound, unknown.Error!.Code);

        var badDuration = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 100);
        Assert.False(badDuration.Success);
        Assert.Equal("PLANNED_DURATION_OUT_OF_RANGE", badDuration.Error!.Code);
    }

    [Fact]
    public async Task Task_snapshots_are_captured_at_start_and_survive_renames()
    {
        var harness = new TestHarness();
        var subjectId = (await harness.CreateSubject.HandleAsync(harness.Profile.Id, "History", null)).Id!.Value;
        var taskId = (await harness.CreateTask.HandleAsync(
            harness.Profile.Id, "Read chapter 4", subjectId: subjectId)).Id!.Value;

        var sessionId = harness.StartSessionAndGetId(taskId: taskId);
        var task = await harness.Tasks.FindAsync(taskId);
        await harness.UpdateTask.HandleAsync(taskId, "Renamed chapter", null, subjectId, null, null, TaskPriority.Normal, null);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal("Read chapter 4", session!.TaskTitleSnapshot);
        Assert.Equal("History", session.SubjectNameSnapshot);
        Assert.Equal("Renamed chapter", task!.Title);
    }

    [Fact]
    public async Task A_task_can_be_linked_again_once_its_previous_session_is_terminal()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId();
        harness.StartSessionAndGetId(taskId: taskId);

        // A second session linking the same task is rejected while the first is
        // non-terminal; the active-profile guard fires first (one profile in MVP).
        // Once the previous session is terminal, the task can be linked again.
        var first = (await harness.Sessions.ListByProfileAsync(harness.Profile.Id)).Single();
        var end = await harness.EndEarly.HandleAsync(first.Id, 5 * 60, 5 * 60, 0, 0, FocusEndReason.UserEnded);
        Assert.True(end.Success);

        // A completed task link is allowed again.
        var second = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60, taskId);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task Blocked_and_paused_transitions_are_recorded_as_events()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();

        await harness.BlockState.HandleAsync(sessionId, blocked: true);
        await harness.BlockState.HandleAsync(sessionId, blocked: false);
        await harness.SystemPause.HandleAsync(sessionId, paused: true);
        await harness.SystemPause.HandleAsync(sessionId, paused: false);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(new[]
        {
            SessionEventType.Start,
            SessionEventType.Blocked,
            SessionEventType.Unblocked,
            SessionEventType.SystemPaused,
            SessionEventType.SystemResumed,
        }, events.Select(e => e.EventType));
        Assert.Equal(Enumerable.Range(1, 5), events.Select(e => e.Sequence)); // monotonic 1..5
    }

    [Fact]
    public async Task Invalid_transitions_return_stable_errors()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();

        var unblock = await harness.BlockState.HandleAsync(sessionId, blocked: false);
        Assert.False(unblock.Success);
        Assert.Equal("SESSION_TRANSITION_INVALID", unblock.Error!.Code); // not Blocked

        var resume = await harness.SystemPause.HandleAsync(sessionId, paused: false);
        Assert.False(resume.Success);
        Assert.Equal("SESSION_TRANSITION_INVALID", resume.Error!.Code); // not SystemPaused
    }

    [Fact]
    public async Task Breaking_out_of_a_focus_session_requires_no_focus_session()
    {
        var harness = new TestHarness();
        harness.StartSessionAndGetId();

        var breakResult = await harness.StartBreak.HandleAsync(harness.Profile.Id, 5 * 60);

        Assert.False(breakResult.Success);
        Assert.Equal(UseCaseErrorCodes.ActiveFocusSessionExists, breakResult.Error!.Code);
    }

    [Fact]
    public async Task Break_completion_awards_no_xp_and_does_not_touch_the_streak()
    {
        var harness = new TestHarness();
        var breakResult = await harness.StartBreak.HandleAsync(harness.Profile.Id, 5 * 60);
        var finish = await harness.FinishBreak.HandleAsync(breakResult.Id!.Value, 5 * 60);

        Assert.True(finish.Success);
        Assert.Empty(harness.Ledger.All);
        var progress = await harness.GetProgress.HandleAsync(harness.Profile.Id);
        Assert.Equal(0, progress!.CurrentStreakDays);
    }

    [Fact]
    public async Task Second_active_break_is_rejected()
    {
        var harness = new TestHarness();
        await harness.StartBreak.HandleAsync(harness.Profile.Id, 5 * 60);
        var second = await harness.StartBreak.HandleAsync(harness.Profile.Id, 10 * 60);
        Assert.False(second.Success);
        Assert.Equal(UseCaseErrorCodes.ActiveBreakSessionExists, second.Error!.Code);
    }

    // ---- Temporary exceptions (§7.3) ----

    [Fact]
    public async Task Balanced_allows_two_exceptions_of_five_minutes_then_rejects_the_third()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced");

        var first = await harness.GrantException.HandleAsync(sessionId, ExceptionTargetType.Website, "youtube.com", ExceptionReasonCategory.StudyResource);
        var second = await harness.GrantException.HandleAsync(sessionId, ExceptionTargetType.Application, "game.exe", ExceptionReasonCategory.Emergency);
        var third = await harness.GrantException.HandleAsync(sessionId, ExceptionTargetType.Website, "reddit.com", ExceptionReasonCategory.Other);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(third.Success);
        Assert.Equal(UseCaseErrorCodes.ExceptionLimitReached, third.Error!.Code);

        var granted = harness.Exceptions.All.Single(e => e.TargetId == "youtube.com");
        Assert.Equal(TimeSpan.FromMinutes(5), granted.ExpiresAtUtc - granted.GrantedAtUtc);
    }

    [Fact]
    public async Task Strict_exceptions_wait_30_seconds_and_last_two_minutes()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Strict");

        var granted = await harness.GrantException.HandleAsync(sessionId, ExceptionTargetType.Application, "game.exe", ExceptionReasonCategory.Other);

        Assert.True(granted.Success);
        var exception = harness.Exceptions.All.Single();
        Assert.Equal(TimeSpan.FromSeconds(150), exception.ExpiresAtUtc - exception.GrantedAtUtc); // 30 delay + 120 duration
    }

    [Fact]
    public async Task Flexible_has_no_exceptions()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Flexible");

        var granted = await harness.GrantException.HandleAsync(sessionId, ExceptionTargetType.Website, "youtube.com", ExceptionReasonCategory.Technical);

        Assert.False(granted.Success);
        Assert.Equal(UseCaseErrorCodes.ExceptionLimitReached, granted.Error!.Code);
    }

    // ---- Blocking rules (§7.1–7.2) ----

    [Fact]
    public async Task Protected_targets_cannot_be_blocked()
    {
        var harness = new TestHarness();

        var protectedApp = await harness.AddBlockedApp.HandleAsync(harness.Profile.Id, "Agent", null, "BoundlessStudy.Agent.exe");
        var systemApp = await harness.AddBlockedApp.HandleAsync(harness.Profile.Id, "System", null, "csrss.exe");

        Assert.False(protectedApp.Success);
        Assert.Equal("BLOCK_TARGET_PROTECTED", protectedApp.Error!.Code);
        Assert.False(systemApp.Success);
    }

    [Fact]
    public async Task Duplicate_blocked_sites_are_rejected_after_normalization()
    {
        var harness = new TestHarness();
        var first = await harness.AddBlockedSite.HandleAsync(harness.Profile.Id, "www.Example.com", null);
        var duplicate = await harness.AddBlockedSite.HandleAsync(harness.Profile.Id, "example.com", null);

        Assert.True(first.Success);
        Assert.False(duplicate.Success);
        Assert.Equal(UseCaseErrorCodes.DuplicateRule, duplicate.Error!.Code);
    }

    [Fact]
    public async Task A_domain_can_be_allowed_and_blocked_at_the_same_time()
    {
        var harness = new TestHarness();

        Assert.True((await harness.AddBlockedSite.HandleAsync(harness.Profile.Id, "example.com", null)).Success);
        Assert.True((await harness.AddAllowedSite.HandleAsync(harness.Profile.Id, "example.com", "Allowed override")).Success);

        // Same list type still rejects duplicates.
        var duplicateAllowed = await harness.AddAllowedSite.HandleAsync(harness.Profile.Id, "www.example.com", null);
        Assert.False(duplicateAllowed.Success);
        Assert.Equal(UseCaseErrorCodes.DuplicateRule, duplicateAllowed.Error!.Code);
    }

    [Fact]
    public async Task Invalid_domains_are_rejected_with_stable_codes()
    {
        var harness = new TestHarness();
        var invalid = await harness.AddBlockedSite.HandleAsync(harness.Profile.Id, "https://example.com/path", null);
        Assert.False(invalid.Success);
        Assert.Equal("DOMAIN_NOT_A_HOST", invalid.Error!.Code);
    }

    // ---- Progress query ----

    [Fact]
    public async Task Progress_query_derives_total_level_streak_and_daily_cap()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        harness.Complete(sessionId, activeMinutes: 25, plannedMinutes: 25);

        var progress = await harness.GetProgress.HandleAsync(harness.Profile.Id);

        Assert.NotNull(progress);
        Assert.Equal(125, progress!.TotalXp);           // 75 session + 50 ACH-001
        Assert.Equal(1, progress.Level);
        Assert.Equal(1, progress.CurrentStreakDays);
        Assert.Equal(1, progress.LongestStreakDays);
        Assert.Equal(1, progress.QualifiedSessionCount);
        Assert.Equal(75, progress.SessionXpToday);
        Assert.Equal(225, progress.SessionCapRemainingToday);
    }

    [Fact]
    public async Task Progress_query_returns_null_for_an_unknown_profile()
    {
        var harness = new TestHarness();
        Assert.Null(await harness.GetProgress.HandleAsync(Guid.NewGuid()));
    }
}
