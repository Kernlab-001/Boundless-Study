using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Pending-interruption recovery (A6, approved 2026-11-19): look up, resume or
/// discard, automatic abandonment at exactly 24 hours, and idempotent terminal
/// rewards. All times are driven by a fixed clock.
/// </summary>
public class SessionRecoveryTests
{
    private const int ActiveMinutes = 10;
    private static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(24);

    private static (TestHarness Harness, Guid SessionId, DateTime InterruptedAt) InterruptedSession()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(ActiveMinutes);
        var checkpoint = harness.Checkpoint.HandleAsync(
            sessionId, ActiveMinutes * 60, ActiveMinutes * 60, 0, 0, 0, 0, 0).GetAwaiter().GetResult();
        Assert.True(checkpoint.Success, checkpoint.Error?.Message);

        var interruptedAt = harness.Clock.UtcNow;
        var interrupt = harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash).GetAwaiter().GetResult();
        Assert.True(interrupt.Success, interrupt.Error?.Message);
        return (harness, sessionId, interruptedAt);
    }

    [Fact]
    public async Task Pending_query_returns_the_interruption_until_it_is_resolved()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();

        Assert.Null(await harness.PendingSession.HandleAsync(Guid.NewGuid()));

        var pending = await harness.PendingSession.HandleAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.Equal(sessionId, pending.SessionId);
        Assert.Equal(interruptedAt, pending.InterruptedAtUtc);
        Assert.Equal(interruptedAt, pending.EffectiveInterruptionAnchorUtc);
        Assert.Equal(interruptedAt.Add(RecoveryLifetime), pending.ExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, pending.Age);
        Assert.False(pending.IsExpired);

        Assert.True((await harness.Abandon.HandleAsync(sessionId, FocusEndReason.Unknown)).Success);
        Assert.Null(await harness.PendingSession.HandleAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task With_one_second_left_the_session_can_still_be_resumed()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);

        var pending = await harness.PendingSession.HandleAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.False(pending.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(86399), pending.Age);

        var resumed = await harness.RecoverSession.HandleAsync(sessionId);

        Assert.True(resumed.Success);
        Assert.True(resumed.Changed);
        var session = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Null(session.InterruptedAtUtc);
        Assert.Equal(1, session.RecoveryCount);
        Assert.Empty(harness.Ledger.All);
    }

    [Theory]
    [InlineData(24 * 60 * 60)]
    [InlineData(24 * 60 * 60 + 1)]
    public async Task At_and_after_twenty_four_hours_a_new_start_auto_abandons_and_succeeds(int ageSeconds)
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.AddSeconds(ageSeconds);

        var start = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.True(start.Success, start.Error?.Message);

        var oldSession = await harness.Sessions.FindAsync(sessionId);
        Assert.Equal(SessionState.Abandoned, oldSession!.State);
        Assert.Equal(FocusEndReason.Unknown, oldSession.EndReason);
        Assert.Equal(interruptedAt, oldSession.InterruptedAtUtc);
        Assert.Equal(15, harness.Ledger.All.Single(e =>
            e.SourceType == LedgerSourceType.SessionPartial && e.IdempotencyKey.StartsWith("SessionPartial:", StringComparison.Ordinal)).Amount);

        var events = harness.Events.All.Where(e => e.SessionId == sessionId).OrderBy(e => e.Sequence).Select(e => e.EventType);
        Assert.Equal(
            new[] { SessionEventType.Start, SessionEventType.Interrupted, SessionEventType.Abandoned, SessionEventType.RewardCreated },
            events);

        var newSession = await harness.Sessions.FindAsync(start.Id!.Value);
        Assert.Equal(SessionState.Active, newSession!.State);
        Assert.Single(harness.Sessions.All, s => s.IsActive);
        Assert.Null(await harness.PendingSession.HandleAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Clock_skew_before_the_interruption_is_zero_age_and_never_expires()
    {
        var (harness, _, interruptedAt) = InterruptedSession();

        harness.Clock.UtcNow = interruptedAt.AddHours(-2);

        var pending = await harness.PendingSession.HandleAsync(harness.Profile.Id);
        Assert.NotNull(pending);
        Assert.Equal(TimeSpan.Zero, pending.Age);
        Assert.False(pending.IsExpired);

        var start = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.False(start.Success);
        Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, start.Error!.Code);
    }

    [Fact]
    public async Task At_exactly_twenty_four_hours_start_is_still_blocked_before_the_boundary_only()
    {
        var (harness, _, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddSeconds(-1);

        var blocked = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.False(blocked.Success);
        Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, blocked.Error!.Code);

        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);
        var allowed = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.True(allowed.Success, allowed.Error?.Message);
    }

    [Fact]
    public async Task Resuming_at_the_boundary_abandons_instead_of_recovering()
    {
        var (harness, sessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime);

        var result = await harness.RecoverSession.HandleAsync(sessionId);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(15, result.XpAwarded);
        Assert.Equal(SessionState.Abandoned, (await harness.Sessions.FindAsync(sessionId))!.State);
        Assert.Equal(1, harness.Ledger.All.Count(e => e.SourceType == LedgerSourceType.SessionPartial));
    }

    [Fact]
    public async Task Resume_discard_and_repeated_start_never_duplicate_a_terminal_reward()
    {
        var (harness, oldSessionId, interruptedAt) = InterruptedSession();
        harness.Clock.UtcNow = interruptedAt.Add(RecoveryLifetime).AddMinutes(1);

        var firstStart = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);
        Assert.True(firstStart.Success, firstStart.Error?.Message);

        var ledgerCount = harness.Ledger.All.Count;
        var totalXp = await harness.Ledger.SumXpAsync(harness.Profile.Id);

        var discardAgain = await harness.Abandon.HandleAsync(oldSessionId, FocusEndReason.Unknown);
        var resumeAgain = await harness.RecoverSession.HandleAsync(oldSessionId);
        var secondStart = await harness.StartSession.HandleAsync(harness.Profile.Id, "Balanced", 25 * 60);

        Assert.True(discardAgain.Success);
        Assert.False(discardAgain.Changed);
        Assert.True(resumeAgain.Success);
        Assert.False(resumeAgain.Changed);
        Assert.False(secondStart.Success);
        Assert.Equal(UseCaseErrorCodes.ActiveFocusSessionExists, secondStart.Error!.Code);
        Assert.Equal(ledgerCount, harness.Ledger.All.Count);
        Assert.Equal(totalXp, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Single(harness.Ledger.All, e => e.SourceType == LedgerSourceType.SessionPartial);
    }
}
