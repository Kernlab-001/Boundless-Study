using System.Text.Json;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Unit and integration tests for Session Events, System State Tracking, Violation Recording,
/// Privacy Compliance (BS-APP-008), and Protection Gap Tracking.
/// </summary>
public class SessionEventsAndSystemStateTests
{
    [Fact]
    public async Task Violation_recording_persists_blocked_event_and_updates_session_state()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();

        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var result = await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            targetId: "discord.exe",
            targetType: "Application");

        Assert.True(result.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Blocked, session.State);
        Assert.Equal(1, session.ViolationCount);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(2, events.Count); // Start (seq 1), Blocked (seq 2)
        Assert.Equal(SessionEventType.Start, events[0].EventType);
        Assert.Equal(SessionEventType.Blocked, events[1].EventType);
        Assert.Equal(2, events[1].Sequence);

        var payload = JsonDocument.Parse(events[1].PayloadJson);
        Assert.Equal("discord.exe", payload.RootElement.GetProperty("targetId").GetString());
        Assert.Equal("Application", payload.RootElement.GetProperty("targetType").GetString());
    }

    [Fact]
    public async Task Violation_recording_persists_unblocked_event_and_resumes_active_session()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        // Block first
        await handler.HandleAsync(sessionId, SessionEventType.Blocked, "steam.exe", "Application");

        // Then unblock
        var unblockResult = await handler.HandleAsync(sessionId, SessionEventType.Unblocked, "steam.exe", "Application");
        Assert.True(unblockResult.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal(1, session.ViolationCount); // ViolationCount was incremented on Blocked

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(3, events.Count);
        Assert.Equal(SessionEventType.Unblocked, events[2].EventType);
        Assert.Equal(3, events[2].Sequence);
    }

    [Fact]
    public async Task Enforcement_failed_and_exception_granted_events_are_recorded_correctly()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var failedResult = await handler.HandleAsync(
            sessionId,
            SessionEventType.EnforcementFailed,
            targetId: "overlay",
            targetType: "System",
            payloadJson: "{\"targetId\":\"overlay\",\"error\":\"Failed to display overlay\"}");
        Assert.True(failedResult.Success);

        var grantedResult = await handler.HandleAsync(
            sessionId,
            SessionEventType.ExceptionGranted,
            targetId: "calculator.exe",
            targetType: "Application");
        Assert.True(grantedResult.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(1, session.ExceptionCount);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(3, events.Count);
        Assert.Equal(SessionEventType.EnforcementFailed, events[1].EventType);
        Assert.Equal(SessionEventType.ExceptionGranted, events[2].EventType);
    }

    [Fact]
    public async Task Unsupported_violation_event_types_are_rejected()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var result = await handler.HandleAsync(sessionId, SessionEventType.RewardCreated, "target");
        Assert.False(result.Success);
        Assert.Equal(UseCaseErrorCodes.DomainRuleViolation, result.Error!.Code);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Single(events); // Only Start event exists
    }

    [Fact]
    public async Task Payload_length_enforcement_succeeds_at_2000_chars_and_fails_at_2001_chars()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        // Construct valid JSON of exactly 2,000 characters
        string prefix = "{\"targetId\":\"app.exe\",\"details\":\"";
        string suffix = "\"}";
        int fillerLen = 2000 - prefix.Length - suffix.Length;
        string valid2000Json = prefix + new string('a', fillerLen) + suffix;
        Assert.Equal(2000, valid2000Json.Length);

        var validResult = await handler.HandleAsync(sessionId, SessionEventType.Blocked, "app.exe", payloadJson: valid2000Json);
        Assert.True(validResult.Success);

        // Construct JSON of 2,001 characters
        harness.UnitOfWork.ResetCounts();
        string invalid2001Json = prefix + new string('a', fillerLen + 1) + suffix;
        Assert.Equal(2001, invalid2001Json.Length);

        var invalidResult = await handler.HandleAsync(sessionId, SessionEventType.Blocked, "app.exe", payloadJson: invalid2001Json);
        Assert.False(invalidResult.Success);
        Assert.Equal("EVENT_PAYLOAD_TOO_LARGE", invalidResult.Error!.Code);
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task Strict_privacy_rejection_of_window_titles_and_urls_with_paths()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        // Window title in payload
        var titleResult = await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            "chrome.exe",
            payloadJson: "{\"windowTitle\":\"Confidential Project Document - Google Docs\"}");
        Assert.False(titleResult.Success);
        Assert.Equal(UseCaseErrorCodes.DomainRuleViolation, titleResult.Error!.Code);

        // URL with path in payload
        var urlResult = await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            "chrome.exe",
            payloadJson: "{\"url\":\"https://reddit.com/r/all/comments/12345\"}");
        Assert.False(urlResult.Success);
        Assert.Equal(UseCaseErrorCodes.DomainRuleViolation, urlResult.Error!.Code);

        // Document content in payload
        var docResult = await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            "word.exe",
            payloadJson: "{\"content\":\"User private notes and passwords\"}");
        Assert.False(docResult.Success);
        Assert.Equal(UseCaseErrorCodes.DomainRuleViolation, docResult.Error!.Code);

        // Ensure nothing was committed or persisted
        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Single(events); // Only Start event
    }

    [Fact]
    public async Task Strict_privacy_target_sanitization_removes_personal_paths_and_urls()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var handler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        // Pass path containing personal username directory
        await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            targetId: @"C:\Users\SecretUser\AppData\Local\Discord\discord.exe",
            targetType: "Application");

        // Pass full URL containing query parameters and paths
        await handler.HandleAsync(
            sessionId,
            SessionEventType.Blocked,
            targetId: "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s",
            targetType: "Website");

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(3, events.Count);

        var payload1 = JsonDocument.Parse(events[1].PayloadJson);
        var target1 = payload1.RootElement.GetProperty("targetId").GetString();
        Assert.Equal("discord.exe", target1);
        Assert.DoesNotContain("SecretUser", events[1].PayloadJson);

        var payload2 = JsonDocument.Parse(events[2].PayloadJson);
        var target2 = payload2.RootElement.GetProperty("targetId").GetString();
        Assert.Equal("youtube.com", target2);
        Assert.DoesNotContain("watch", events[2].PayloadJson);
        Assert.DoesNotContain("dQw4w9WgXcQ", events[2].PayloadJson);
    }

    [Fact]
    public async Task Workstation_lock_and_unlock_transitions_are_handled_correctly()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);
        var lockOccurredAtUtc = harness.Clock.UtcNow.AddSeconds(10);
        harness.Clock.UtcNow = lockOccurredAtUtc.AddSeconds(20);

        // Lock workstation
        var lockResult = await stateCases.HandleWorkstationLockAsync(
            sessionId,
            isLocked: true,
            occurredAtUtc: lockOccurredAtUtc);
        Assert.True(lockResult.Success);
        Assert.True(lockResult.Changed);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.SystemPaused, session.State);

        // Idempotent second lock
        var secondLock = await stateCases.HandleWorkstationLockAsync(sessionId, isLocked: true);
        Assert.True(secondLock.Success);
        Assert.False(secondLock.Changed);

        // Unlock workstation
        var unlockResult = await stateCases.HandleWorkstationLockAsync(sessionId, isLocked: false);
        Assert.True(unlockResult.Success);
        Assert.True(unlockResult.Changed);

        session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session.State);

        // Idempotent second unlock
        var secondUnlock = await stateCases.HandleWorkstationLockAsync(sessionId, isLocked: false);
        Assert.True(secondUnlock.Success);
        Assert.False(secondUnlock.Changed);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(new[]
        {
            SessionEventType.Start,
            SessionEventType.SystemPaused,
            SessionEventType.SystemResumed,
        }, events.Select(e => e.EventType).ToArray());
        Assert.Equal(lockOccurredAtUtc, events[1].OccurredAtUtc);
    }

    [Fact]
    public async Task System_sleep_and_resume_transitions_follow_power_mode_rules()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);
        var sleepOccurredAtUtc = harness.Clock.UtcNow.AddSeconds(15);
        harness.Clock.UtcNow = sleepOccurredAtUtc.AddSeconds(30);

        // Suspend (sleep)
        var sleepResult = await stateCases.HandleSystemSleepAsync(
            sessionId,
            isSuspended: true,
            occurredAtUtc: sleepOccurredAtUtc);
        Assert.True(sleepResult.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.SystemPaused, session.State);

        // Resume
        var resumeResult = await stateCases.HandleSystemSleepAsync(sessionId, isSuspended: false);
        Assert.True(resumeResult.Success);

        session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session.State);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(sleepOccurredAtUtc, events[1].OccurredAtUtc);
    }

    [Fact]
    public async Task User_idle_events_and_excess_calculation()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var idleStartedAtUtc = harness.Clock.UtcNow.AddSeconds(15);
        harness.Clock.UtcNow = idleStartedAtUtc.AddSeconds(10);
        // User goes idle
        var idleStartResult = await stateCases.HandleUserIdleAsync(
            sessionId,
            isIdle: true,
            TimeSpan.FromMinutes(5),
            occurredAtUtc: idleStartedAtUtc);
        Assert.True(idleStartResult.Success);

        // User returns after 8 minutes (480s). Threshold is 5 min (300s), so excess = 180s.
        var idleEndedAtUtc = idleStartedAtUtc.AddMinutes(8);
        harness.Clock.UtcNow = idleEndedAtUtc.AddSeconds(5);
        var idleEndResult = await stateCases.HandleUserIdleAsync(
            sessionId,
            isIdle: false,
            TimeSpan.FromMinutes(8),
            occurredAtUtc: idleEndedAtUtc);
        Assert.True(idleEndResult.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(180, session.IdleSeconds);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(3, events.Count);
        Assert.Equal(SessionEventType.IdleStarted, events[1].EventType);
        Assert.Equal(SessionEventType.IdleEnded, events[2].EventType);
        Assert.Equal(idleStartedAtUtc, events[1].OccurredAtUtc);
        Assert.Equal(idleEndedAtUtc, events[2].OccurredAtUtc);
    }

    [Fact]
    public async Task Duplicate_idle_notifications_do_not_duplicate_events_or_idle_seconds()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        Assert.True((await stateCases.HandleUserIdleAsync(sessionId, true, TimeSpan.FromMinutes(5))).Success);
        var duplicateStart = await stateCases.HandleUserIdleAsync(sessionId, true, TimeSpan.FromMinutes(6));
        Assert.True(duplicateStart.Success);
        Assert.False(duplicateStart.Changed);

        Assert.True((await stateCases.HandleUserIdleAsync(sessionId, false, TimeSpan.FromMinutes(8))).Success);
        var duplicateEnd = await stateCases.HandleUserIdleAsync(sessionId, false, TimeSpan.FromMinutes(8));
        Assert.True(duplicateEnd.Success);
        Assert.False(duplicateEnd.Changed);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(180, session.IdleSeconds);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(new[]
        {
            SessionEventType.Start,
            SessionEventType.IdleStarted,
            SessionEventType.IdleEnded,
        }, events.Select(e => e.EventType));
    }

    [Fact]
    public async Task Negative_idle_duration_is_rejected_without_recording_event()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var result = await stateCases.HandleUserIdleAsync(sessionId, true, TimeSpan.FromSeconds(-1));

        Assert.False(result.Success);
        Assert.Equal(UseCaseErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Single(await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId));
    }

    [Fact]
    public async Task Protection_gap_status_changes_and_gap_seconds_accumulation()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        // Initially establish Protected status
        var initial = await stateCases.HandleProtectionStatusChangedAsync(
            sessionId,
            ProtectionStatus.Protected,
            VerificationStatus.Verified);
        Assert.True(initial.Success);

        // Status degrades: leaves Protected
        var degraded = await stateCases.HandleProtectionStatusChangedAsync(
            sessionId,
            ProtectionStatus.Degraded,
            VerificationStatus.Degraded);
        Assert.True(degraded.Success);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(ProtectionStatus.Degraded, session.ProtectionStatus);

        // Record a computed protection gap of 45 seconds
        var gap1 = await stateCases.RecordProtectionGapAsync(sessionId, 45);
        Assert.True(gap1.Success);

        // Record another gap of 30 seconds
        var gap2 = await stateCases.RecordProtectionGapAsync(sessionId, 30);
        Assert.True(gap2.Success);

        session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(75, session.ProtectionGapSeconds);

        // Status restored to Protected
        var restored = await stateCases.HandleProtectionStatusChangedAsync(
            sessionId,
            ProtectionStatus.Protected,
            VerificationStatus.Verified);
        Assert.True(restored.Success);

        session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(ProtectionStatus.Protected, session.ProtectionStatus);

        var events = await harness.Events.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Contains(events, e => e.EventType == SessionEventType.ProtectionDegraded);
        Assert.Contains(events, e => e.EventType == SessionEventType.ProtectionRestored);
    }

    [Fact]
    public async Task Transactional_safety_rolls_back_on_invalid_session_or_terminal_state()
    {
        var harness = new TestHarness();
        var stateCases = new SystemStateUseCases(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);
        var violationHandler = new RecordViolationHandler(harness.Sessions, harness.UnitOfWork, harness.Clock, harness.EventRecorder);

        var nonExistentId = Guid.NewGuid();

        var notFoundViolation = await violationHandler.HandleAsync(nonExistentId, SessionEventType.Blocked, "target.exe");
        Assert.False(notFoundViolation.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotFound, notFoundViolation.Error!.Code);

        var notFoundState = await stateCases.HandleWorkstationLockAsync(nonExistentId, isLocked: true);
        Assert.False(notFoundState.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotFound, notFoundState.Error!.Code);

        // Start session and end it
        var sessionId = harness.StartSessionAndGetId();
        await harness.EndEarly.HandleAsync(sessionId, 5 * 60, 5 * 60, 0, 0, FocusEndReason.UserEnded);

        // Attempting violation on terminal session is rejected
        var terminalViolation = await violationHandler.HandleAsync(sessionId, SessionEventType.Blocked, "target.exe");
        Assert.False(terminalViolation.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotActive, terminalViolation.Error!.Code);

        // Attempting lock on terminal session is rejected
        var terminalLock = await stateCases.HandleWorkstationLockAsync(sessionId, isLocked: true);
        Assert.False(terminalLock.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotActive, terminalLock.Error!.Code);
    }

}
