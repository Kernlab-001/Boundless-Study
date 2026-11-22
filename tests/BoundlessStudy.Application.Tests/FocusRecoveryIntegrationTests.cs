using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Comprehensive integration and regression tests for Problem 4 (Resume/Discard recovery interface).
/// Verifies:
/// 1. Querying pending recoverable session state (anchor, age, remaining time, expired status).
/// 2. Resume command handling (resuming session, completing if target duration reached, auto-abandoning if expired).
/// 3. Discard command handling (abandoning session idempotently, awarding partial XP once, duplicate discard returning unchanged).
/// 4. Prevention of new session start while a recoverable session is pending, and auto-abandoning when expired.
/// 5. Client abstractions and adapters (in-process and framed wire protocol).
/// </summary>
public class FocusRecoveryIntegrationTests
{
    private const int ActiveMinutes = 10;
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(24);

    private static (TestHarness Harness, Guid SessionId, DateTime InterruptedAt) CreateInterruptedSession(
        int activeMinutes = ActiveMinutes,
        int plannedMinutes = 25)
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: plannedMinutes);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(activeMinutes);

        var checkpoint = harness.Checkpoint.HandleAsync(
            sessionId, activeMinutes * 60, activeMinutes * 60, 0, 0, 0, 0, 0).GetAwaiter().GetResult();
        Assert.True(checkpoint.Success, checkpoint.Error?.Message);

        var interruptedAt = harness.Clock.UtcNow;
        var interrupt = harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash).GetAwaiter().GetResult();
        Assert.True(interrupt.Success, interrupt.Error?.Message);

        return (harness, sessionId, interruptedAt);
    }

    #region Query Tests

    [Fact]
    public async Task Query_WhenNoSessionPending_ReturnsNull()
    {
        var harness = new TestHarness();

        var pending = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.Null(pending);

        Assert.True(await harness.RecoveryCoordinator.CanStartNewSessionAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Query_WhenProfileNotFound_ReturnsNull()
    {
        var (harness, _, _) = CreateInterruptedSession();

        var pending = await harness.RecoveryCoordinator.GetPendingAsync(Guid.NewGuid());
        Assert.Null(pending);
    }

    [Fact]
    public async Task Query_WhenSessionPending_ReturnsAnchorAgeRemainingTimeAndExpiredStatus()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();

        // Check immediately after interruption (T0)
        var pendingAtT0 = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.NotNull(pendingAtT0);
        Assert.Equal(sessionId, pendingAtT0.SessionId);
        Assert.Equal(interruptedAt, pendingAtT0.EffectiveInterruptionAnchorUtc);
        Assert.Equal(interruptedAt, pendingAtT0.InterruptedAtUtc);
        Assert.Equal(interruptedAt + RecoveryLifetime, pendingAtT0.ExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, pendingAtT0.Age);
        Assert.Equal(RecoveryLifetime, pendingAtT0.RemainingTime);
        Assert.Equal(RecoveryLifetime, pendingAtT0.RemainingRecoveryTime);
        Assert.False(pendingAtT0.IsExpired);

        // Advance 4 hours
        harness.Clock.UtcNow = interruptedAt.AddHours(4);
        var pendingAt4H = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.NotNull(pendingAt4H);
        Assert.Equal(TimeSpan.FromHours(4), pendingAt4H.Age);
        Assert.Equal(TimeSpan.FromHours(20), pendingAt4H.RemainingTime);
        Assert.Equal(TimeSpan.FromHours(20), pendingAt4H.RemainingRecoveryTime);
        Assert.False(pendingAt4H.IsExpired);

        // Advance to 1 second before expiry
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);
        var pendingAt23H59M59S = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.NotNull(pendingAt23H59M59S);
        Assert.Equal(TimeSpan.FromSeconds(86399), pendingAt23H59M59S.Age);
        Assert.Equal(TimeSpan.FromSeconds(1), pendingAt23H59M59S.RemainingTime);
        Assert.False(pendingAt23H59M59S.IsExpired);

        // Advance to exactly 24 hours (inclusive expiry boundary)
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);
        var pendingAt24H = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.NotNull(pendingAt24H);
        Assert.Equal(RecoveryLifetime, pendingAt24H.Age);
        Assert.Equal(TimeSpan.Zero, pendingAt24H.RemainingTime);
        Assert.True(pendingAt24H.IsExpired);
    }

    [Fact]
    public async Task Query_WithClockSkewBeforeAnchor_ClampsAgeToZeroAndRemainingTimeToFullLifetime()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();

        // Clock is skewed 3 hours behind recorded anchor
        harness.Clock.UtcNow = interruptedAt.AddHours(-3);

        var pending = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.Equal(sessionId, pending.SessionId);
        Assert.Equal(TimeSpan.Zero, pending.Age);
        Assert.Equal(RecoveryLifetime, pending.RemainingTime);
        Assert.False(pending.IsExpired);
    }

    [Fact]
    public async Task Query_ActiveSession_IsNeverReportedAsPending()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var pending = await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id);
        Assert.Null(pending);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
    }

    #endregion

    #region Resume Command Tests

    [Fact]
    public async Task Resume_WhenValidUnder24Hours_ResumesSessionToActiveAndIncrementsRecoveryCount()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddHours(2);

        var outcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);

        Assert.True(outcome.Success);
        Assert.True(outcome.Changed);
        Assert.False(outcome.IsStreakQualified);
        Assert.Equal(0, outcome.XpAwarded);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Null(session.InterruptedAtUtc);
        Assert.Equal(1, session.RecoveryCount);

        // Events must contain Recovered
        var events = harness.Events.All.Where(e => e.SessionId == sessionId).Select(e => e.EventType);
        Assert.Contains(SessionEventType.Recovered, events);

        // Pending query returns null once resumed
        Assert.Null(await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Resume_WhenCheckpointAlreadyMetPlannedDuration_FinalizesSessionAsCompletedWithFullReward()
    {
        // 25-minute planned session, checkpointed at full 25 minutes
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 25, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        var outcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);

        Assert.True(outcome.Success);
        Assert.True(outcome.Changed);
        Assert.True(outcome.IsStreakQualified);
        Assert.True(outcome.XpAwarded > 0);

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Completed, session.State);

        var ledgerEntry = Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionCompletion);
        Assert.Equal(outcome.XpAwarded, ledgerEntry.Amount);

        // Pending query is null
        Assert.Null(await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Resume_WhenSessionExpiredAtOrAfter24Hours_AutoAbandonsAndAwardsPartialXp()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 10, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var outcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);

        Assert.True(outcome.Success);
        Assert.True(outcome.Changed);
        Assert.Equal(15, outcome.XpAwarded); // 10 min * 3 XP/min * 0.5 abandonment multiplier = 15 XP

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);

        var ledgerEntry = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, ledgerEntry.SourceType);
        Assert.Equal(15, ledgerEntry.Amount);

        // Pending query is null
        Assert.Null(await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Resume_WhenSessionNotFound_ReturnsSessionNotFound()
    {
        var harness = new TestHarness();
        var outcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, Guid.NewGuid());

        Assert.False(outcome.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotFound, outcome.Error?.Code);
    }

    [Fact]
    public async Task Resume_WhenSessionNotPending_ReturnsSessionNotPending()
    {
        var harness = new TestHarness();
        var activeSessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, activeSessionId);

        Assert.False(outcome.Success);
        Assert.Equal("SESSION_NOT_PENDING", outcome.Error?.Code);
    }

    [Fact]
    public async Task Resume_WhenAlreadyTerminal_IsIdempotentAndReturnsExistingOutcomeUnchanged()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 10, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // First resume expires and abandons
        var firstResume = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);
        Assert.True(firstResume.Success);
        Assert.True(firstResume.Changed);
        Assert.Equal(15, firstResume.XpAwarded);

        var ledgerCount = harness.Ledger.All.Count;
        var eventCount = harness.Events.All.Count;

        // Second resume: idempotent retry
        var secondResume = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);
        Assert.True(secondResume.Success);
        Assert.False(secondResume.Changed); // No change!
        Assert.Equal(firstResume.XpAwarded, secondResume.XpAwarded);
        Assert.Equal(ledgerCount, harness.Ledger.All.Count);
        Assert.Equal(eventCount, harness.Events.All.Count);
    }

    #endregion

    #region Discard Command Tests

    [Fact]
    public async Task Discard_WhenSessionPending_AbandonsSessionAndAwardsPartialXpOnce()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 10, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        var outcome = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, sessionId);

        Assert.True(outcome.Success);
        Assert.True(outcome.Changed);
        Assert.Equal(15, outcome.XpAwarded); // 10 min * 3 XP/min * 0.5 = 15 XP

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.Equal(FocusEndReason.Unknown, session.EndReason);

        var partialEntry = Assert.Single(harness.Ledger.All);
        Assert.Equal(LedgerSourceType.SessionPartial, partialEntry.SourceType);
        Assert.Equal(15, partialEntry.Amount);

        // Pending query is now null
        Assert.Null(await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Discard_DuplicateCalls_AreIdempotentAndReturnOutcomeUnchangedWithoutDuplicateRewardOrEvents()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 10, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        var first = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, sessionId);
        Assert.True(first.Success);
        Assert.True(first.Changed);

        var ledgerCount = harness.Ledger.All.Count;
        var eventCount = harness.Events.All.Count;
        var totalXp = await harness.Ledger.SumXpAsync(harness.Profile.Id);

        // Second discard: duplicate discard returning unchanged
        var second = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, sessionId);
        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(first.XpAwarded, second.XpAwarded);
        Assert.Equal(totalXp, second.TotalXpAfter);

        // Third discard: still idempotent
        var third = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, sessionId);
        Assert.True(third.Success);
        Assert.False(third.Changed);

        // Ledger entries and events are not duplicated
        Assert.Equal(ledgerCount, harness.Ledger.All.Count);
        Assert.Equal(eventCount, harness.Events.All.Count);
        Assert.Equal(totalXp, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Discard_WhenSessionNotFound_ReturnsSessionNotFound()
    {
        var harness = new TestHarness();
        var outcome = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, Guid.NewGuid());

        Assert.False(outcome.Success);
        Assert.Equal(UseCaseErrorCodes.SessionNotFound, outcome.Error?.Code);
    }

    [Fact]
    public async Task Discard_WhenSessionNotPending_ReturnsSessionNotPending()
    {
        var harness = new TestHarness();
        var activeSessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);

        var outcome = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, activeSessionId);

        Assert.False(outcome.Success);
        Assert.Equal("SESSION_NOT_PENDING", outcome.Error?.Code);
    }

    [Fact]
    public async Task Discard_AfterSessionResumedAndCompleted_ReturnsExistingCompletionOutcomeUnchanged()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession(activeMinutes: 25, plannedMinutes: 25);
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        var resumeOutcome = await harness.RecoveryCoordinator.ResumeAsync(harness.Profile.Id, sessionId);
        Assert.True(resumeOutcome.Success);
        Assert.True(resumeOutcome.Changed);

        // Discard against already completed session is idempotent
        var discardOutcome = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, sessionId);
        Assert.True(discardOutcome.Success);
        Assert.False(discardOutcome.Changed);
        Assert.Equal(resumeOutcome.XpAwarded, discardOutcome.XpAwarded);
    }

    #endregion

    #region Prevention of New Session Start Tests

    [Fact]
    public async Task StartNewSession_WhenRecoverableSessionPending_IsPreventedWithRecoverableSessionPendingError()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        // Start prevention check via coordinator
        Assert.False(await harness.RecoveryCoordinator.CanStartNewSessionAsync(harness.Profile.Id));

        // Start attempt fails
        var startResult = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.False(startResult.Success);
        Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, startResult.Error?.Code);
        Assert.Contains("Resume or discard", startResult.Error?.Message ?? string.Empty);

        // Original session remains RecoverableInterrupted
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
    }

    [Fact]
    public async Task StartNewSession_WhenRecoverableSessionIsExpiredAtOrAfter24Hours_AutoAbandonsOldSessionAndStartsNewSession()
    {
        var (harness, oldSessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        // At 24 hours, coordinator confirms new session can start because pending is expired
        Assert.True(await harness.RecoveryCoordinator.CanStartNewSessionAsync(harness.Profile.Id));

        // Starting a new session automatically abandons the expired one
        var startResult = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.True(startResult.Success, startResult.Error?.Message);
        var newSessionId = startResult.Id!.Value;
        Assert.NotEqual(oldSessionId, newSessionId);

        // Old session was abandoned with partial XP
        var oldSession = await harness.Sessions.FindAsync(oldSessionId);
        Assert.Equal(SessionState.Abandoned, oldSession!.State);
        Assert.Equal(FocusEndReason.Unknown, oldSession.EndReason);
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);

        // New session is active
        var newSession = await harness.Sessions.FindAsync(newSessionId);
        Assert.Equal(SessionState.Active, newSession!.State);

        // No pending session remaining
        Assert.Null(await harness.RecoveryCoordinator.GetPendingAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task StartNewSession_AfterDiscard_StartsNormally()
    {
        var (harness, oldSessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddMinutes(30);

        // User discards
        var discard = await harness.RecoveryCoordinator.DiscardAsync(harness.Profile.Id, oldSessionId);
        Assert.True(discard.Success);

        // Now user can start a new session
        Assert.True(await harness.RecoveryCoordinator.CanStartNewSessionAsync(harness.Profile.Id));
        var startResult = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.True(startResult.Success, startResult.Error?.Message);

        var newSession = await harness.Sessions.FindAsync(startResult.Id!.Value);
        Assert.Equal(SessionState.Active, newSession!.State);
    }

    #endregion

    #region Client Adapter and Wire Protocol Tests

    [Fact]
    public async Task InProcessClientAdapter_EndToEndWorkflow()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddHours(2);

        IRecoveryClient client = new ApplicationRecoveryClientAdapter(harness.Profile.Id, harness.RecoveryCoordinator);

        // 1. GetState
        var stateResponse = await client.GetStateAsync();
        Assert.True(stateResponse.Success);
        Assert.NotNull(stateResponse.State?.Pending);
        var pending = stateResponse.State.Pending;
        Assert.Equal(sessionId, pending.SessionId);
        Assert.Equal(interruptedAt, pending.AnchorUtc);
        Assert.Equal(7200, pending.AgeSeconds);
        Assert.Equal(TimeSpan.FromHours(2), pending.Age);
        Assert.Equal(TimeSpan.FromHours(22), pending.RemainingTime);
        Assert.Equal(22 * 3600, pending.EffectiveRemainingSeconds);
        Assert.False(pending.IsExpired);

        // 2. Resume
        var resumeResponse = await client.ResumeAsync(sessionId);
        Assert.True(resumeResponse.Success);
        Assert.Null(resumeResponse.State?.Pending); // Resumed to active; no longer pending recovery

        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
    }

    [Fact]
    public async Task InProcessClientAdapter_Discard_IsIdempotent()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddHours(1);

        IRecoveryClient client = new ApplicationRecoveryClientAdapter(harness.Profile.Id, harness.RecoveryCoordinator);

        // 1. Discard
        var firstDiscard = await client.DiscardAsync(sessionId);
        Assert.True(firstDiscard.Success);
        Assert.Null(firstDiscard.State?.Pending);

        // 2. Duplicate discard
        var secondDiscard = await client.DiscardAsync(sessionId);
        Assert.True(secondDiscard.Success);
        Assert.Null(secondDiscard.State?.Pending);

        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task InProcessClientAdapter_AgentUnavailable_ReturnsExpectedError()
    {
        var (harness, sessionId, _) = CreateInterruptedSession();
        var adapter = new ApplicationRecoveryClientAdapter(harness.Profile.Id, harness.RecoveryCoordinator);
        adapter.SetAgentAvailable(false);

        IRecoveryClient client = adapter;
        var response = await client.GetStateAsync();

        Assert.False(response.Success);
        Assert.Equal(RecoveryErrors.AgentUnavailable, response.ErrorCode);

        var resumeResponse = await client.ResumeAsync(sessionId);
        Assert.False(resumeResponse.Success);
        Assert.Equal(RecoveryErrors.AgentUnavailable, resumeResponse.ErrorCode);

        var discardResponse = await client.DiscardAsync(sessionId);
        Assert.False(discardResponse.Success);
        Assert.Equal(RecoveryErrors.AgentUnavailable, discardResponse.ErrorCode);
    }

    [Fact]
    public async Task FramedStreamClientAdapter_EndToEndWireProtocol()
    {
        var (harness, sessionId, interruptedAt) = CreateInterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddHours(3);

        IRecoveryClient client = new FramedStreamRecoveryClientAdapter(harness.Profile.Id, harness.RecoveryCoordinator);

        // 1. Query state over framed stream
        var stateResponse = await client.GetStateAsync();
        Assert.True(stateResponse.Success);
        Assert.NotNull(stateResponse.State?.Pending);
        var pending = stateResponse.State.Pending;
        Assert.Equal(sessionId, pending.SessionId);
        Assert.Equal(interruptedAt, pending.AnchorUtc);
        Assert.Equal(TimeSpan.FromHours(3), pending.Age);
        Assert.Equal(TimeSpan.FromHours(21), pending.RemainingTime);
        Assert.False(pending.IsExpired);

        // 2. Discard over framed stream
        var discardResponse = await client.DiscardAsync(sessionId);
        Assert.True(discardResponse.Success);
        Assert.Null(discardResponse.State?.Pending);

        // 3. Duplicate discard over framed stream (idempotent)
        var repeatDiscard = await client.DiscardAsync(sessionId);
        Assert.True(repeatDiscard.Success);
        Assert.Null(repeatDiscard.State?.Pending);

        Assert.Single(harness.Ledger.All);
    }

    [Fact]
    public async Task FramedStreamClientAdapter_PipeBroken_ReturnsAgentUnavailable()
    {
        var (harness, sessionId, _) = CreateInterruptedSession();
        var adapter = new FramedStreamRecoveryClientAdapter(harness.Profile.Id, harness.RecoveryCoordinator);
        adapter.BreakPipe();

        IRecoveryClient client = adapter;
        var response = await client.GetStateAsync();

        Assert.False(response.Success);
        Assert.Equal(RecoveryErrors.AgentUnavailable, response.ErrorCode);
    }

    [Fact]
    public async Task WireCodec_RequestValidation_RejectsInvalidRequests()
    {
        // Null request
        Assert.False(RecoveryRequestValidator.TryValidate(null, out var error1));
        Assert.NotEmpty(error1);

        // Unsupported version
        var invalidVersion = new RecoveryRequest(999, Guid.NewGuid().ToString(), RecoveryProtocol.GetStateOperation, null);
        Assert.False(RecoveryRequestValidator.TryValidate(invalidVersion, out var error2));
        Assert.Contains("Unsupported protocol version", error2);

        // Invalid request id (not a GUID)
        var invalidReqId = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, "not-a-guid", RecoveryProtocol.GetStateOperation, null);
        Assert.False(RecoveryRequestValidator.TryValidate(invalidReqId, out var error3));
        Assert.Contains("GUID", error3);

        // GetState with a session id
        var getStateWithId = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, Guid.NewGuid().ToString(), RecoveryProtocol.GetStateOperation, Guid.NewGuid());
        Assert.False(RecoveryRequestValidator.TryValidate(getStateWithId, out var error4));
        Assert.Contains("must not name a session", error4);

        // Resume without session id
        var resumeNoId = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, Guid.NewGuid().ToString(), RecoveryProtocol.ResumeOperation, null);
        Assert.False(RecoveryRequestValidator.TryValidate(resumeNoId, out var error5));
        Assert.Contains("session id is required", error5);

        // Discard without session id
        var discardNoId = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, Guid.NewGuid().ToString(), RecoveryProtocol.DiscardOperation, null);
        Assert.False(RecoveryRequestValidator.TryValidate(discardNoId, out var error6));
        Assert.Contains("session id is required", error6);

        // Unknown operation
        var unknownOp = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, Guid.NewGuid().ToString(), "unknown.operation", null);
        Assert.False(RecoveryRequestValidator.TryValidate(unknownOp, out var error7));
        Assert.Contains("Unknown recovery operation", error7);
    }

    [Fact]
    public async Task WireCodec_FrameSizeLimitEnforced()
    {
        using var stream = new MemoryStream();

        // Writing payload exceeding 64 KiB throws InvalidDataException
        var oversizedPayload = new byte[RecoveryProtocol.MaxFrameBytes + 1];
        oversizedPayload[0] = 1;

        // Try write via binary length
        var lengthBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, oversizedPayload.Length);
        await stream.WriteAsync(lengthBytes);
        await stream.WriteAsync(oversizedPayload);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await RecoveryFrameCodec.ReadRequestAsync(stream);
        });
    }

    [Fact]
    public async Task WireCodec_MalformedJson_ReadRequestFrameReturnsMalformedResult()
    {
        // 1. JSON has syntax error: malformed and requestId cannot be parsed
        using var stream1 = new MemoryStream();
        var brokenJson = System.Text.Encoding.UTF8.GetBytes("{\"broken\":");

        var lengthBytes1 = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes1, brokenJson.Length);
        await stream1.WriteAsync(lengthBytes1);
        await stream1.WriteAsync(brokenJson);
        stream1.Position = 0;

        var result1 = await RecoveryFrameCodec.ReadRequestFrameAsync(stream1);
        Assert.NotNull(result1);
        Assert.True(result1.IsMalformed);
        Assert.Null(result1.RequestId);
        Assert.NotEmpty(result1.FailureMessage!);

        // 2. JSON is valid syntax but invalid schema (e.g. numeric operation): requestId is extracted for protocol response
        using var stream2 = new MemoryStream();
        var requestId = Guid.NewGuid().ToString();
        var schemaErrorJson = System.Text.Encoding.UTF8.GetBytes($"{{\"requestId\":\"{requestId}\",\"operation\":12345}}");

        var lengthBytes2 = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes2, schemaErrorJson.Length);
        await stream2.WriteAsync(lengthBytes2);
        await stream2.WriteAsync(schemaErrorJson);
        stream2.Position = 0;

        var result2 = await RecoveryFrameCodec.ReadRequestFrameAsync(stream2);
        Assert.NotNull(result2);
        Assert.True(result2.IsMalformed);
        Assert.Equal(requestId, result2.RequestId);
        Assert.NotEmpty(result2.FailureMessage!);
    }

    #endregion
}
