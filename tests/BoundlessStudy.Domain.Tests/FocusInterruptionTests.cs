using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

/// <summary>
/// The approved interruption-recovery policy (A6, 2026-11-19):
/// a RecoverableInterrupted session is resolved manually, and becomes abandoned
/// automatically once it is at least 24 hours old. The timestamp is deterministic
/// and invalid/future timestamps can never cause premature expiry.
/// </summary>
public class FocusInterruptionTests
{
    private static readonly DateTime StartedAt = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InterruptedAt = StartedAt.AddMinutes(10);

    private static FocusSession Interrupt()
    {
        var session = FocusSession.Start(Guid.NewGuid(), "Balanced", 25 * 60, StartedAt, "ECONOMY-1");
        session.ApplyCheckpoint(InterruptedAt, 600, 600, 0, 0, 0, 0, 0);
        session.MarkRecoverableInterrupted(InterruptedAt, FocusEndReason.AgentCrash);
        return session;
    }

    [Fact]
    public void Marking_interrupted_records_the_dedicated_timestamp()
    {
        var session = Interrupt();

        Assert.Equal(SessionState.RecoverableInterrupted, session.State);
        Assert.Equal(InterruptedAt, session.InterruptedAtUtc);
        Assert.Equal(InterruptedAt, session.EffectiveInterruptionAnchorUtc);
        Assert.Equal(TimeSpan.Zero, session.PendingInterruptionAge(InterruptedAt));
        Assert.False(session.IsPendingInterruptionExpired(InterruptedAt));
    }

    [Theory]
    [InlineData(23 * 60 * 60 + 59 * 60 + 59, false)] // 23:59:59 → may still recover
    [InlineData(24 * 60 * 60, true)]                 // exactly 24:00:00 → expired
    [InlineData(24 * 60 * 60 + 1, true)]             // 24:00:01 → expired
    public void Expiry_boundary_is_inclusive_at_exactly_twenty_four_hours(int ageSeconds, bool expired)
    {
        var session = Interrupt();
        var now = InterruptedAt.AddSeconds(ageSeconds);

        Assert.Equal(TimeSpan.FromSeconds(ageSeconds), session.PendingInterruptionAge(now));
        Assert.Equal(expired, session.IsPendingInterruptionExpired(now));
    }

    [Fact]
    public void Recovery_clears_the_interruption_timestamp_and_age()
    {
        var session = Interrupt();

        session.Recover(InterruptedAt.AddHours(5));

        Assert.Equal(SessionState.Active, session.State);
        Assert.Null(session.InterruptedAtUtc);
        Assert.Equal(TimeSpan.Zero, session.PendingInterruptionAge(InterruptedAt.AddHours(5)));
        Assert.False(session.IsPendingInterruptionExpired(InterruptedAt.AddHours(30)));
        Assert.Equal(1, session.RecoveryCount);
    }

    [Fact]
    public void Abandonment_retains_the_interruption_timestamp_for_audit()
    {
        var session = Interrupt();

        session.Abandon(InterruptedAt.AddHours(24), FocusEndReason.Unknown);

        Assert.Equal(SessionState.Abandoned, session.State);
        Assert.Equal(InterruptedAt, session.InterruptedAtUtc);
        Assert.Equal(InterruptedAt.AddHours(24), session.EndedAtUtc);
    }

    [Fact]
    public void A_future_or_clock_skewed_interruption_timestamp_never_expires_immediately()
    {
        var session = FocusSession.Start(Guid.NewGuid(), "Balanced", 25 * 60, StartedAt, "ECONOMY-1");
        session.MarkRecoverableInterrupted(StartedAt.AddHours(2), FocusEndReason.AgentCrash);

        var skewedNow = StartedAt;

        Assert.Equal(TimeSpan.Zero, session.PendingInterruptionAge(skewedNow));
        Assert.False(session.IsPendingInterruptionExpired(skewedNow));
    }

    [Fact]
    public void A_non_interrupted_session_has_zero_age_and_is_never_expired()
    {
        var active = FocusSession.Start(Guid.NewGuid(), "Balanced", 25 * 60, StartedAt, "ECONOMY-1");
        var completed = FocusSession.Start(Guid.NewGuid(), "Balanced", 300, StartedAt, "ECONOMY-1");
        completed.Complete(StartedAt.AddMinutes(5), 300, 300, 0, 0);

        Assert.Equal(TimeSpan.Zero, active.PendingInterruptionAge(StartedAt.AddDays(3)));
        Assert.False(active.IsPendingInterruptionExpired(StartedAt.AddDays(3)));
        Assert.False(completed.IsPendingInterruptionExpired(StartedAt.AddDays(3)));
    }
}
