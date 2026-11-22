using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

public class BreakSessionTests
{
    [Theory]
    [InlineData(59)]
    [InlineData(3601)]
    public void Planned_break_duration_is_60_to_3600_seconds(int plannedSeconds)
    {
        Assert.Throws<DomainException>(() => BreakSession.Start(Guid.NewGuid(), plannedSeconds, DateTime.UtcNow));
    }

    [Fact]
    public void Start_creates_an_active_break_with_an_optional_link()
    {
        var focusId = Guid.NewGuid();
        var session = BreakSession.Start(Guid.NewGuid(), 5 * 60, DateTime.UtcNow, focusId);

        Assert.Equal(BreakState.Active, session.State);
        Assert.Equal(focusId, session.FocusSessionId);
        Assert.Equal(0, session.ElapsedSeconds);
    }

    [Fact]
    public void Complete_sets_terminal_state()
    {
        var session = BreakSession.Start(Guid.NewGuid(), 5 * 60, DateTime.UtcNow);
        var at = DateTime.UtcNow;

        session.Complete(at, elapsedSeconds: 5 * 60);
        Assert.Equal(BreakState.Completed, session.State);
        Assert.Equal(BreakEndReason.Completed, session.EndReason);
        Assert.True(session.IsTerminal);
    }

    [Fact]
    public void End_early_requires_a_non_completed_reason()
    {
        var session = BreakSession.Start(Guid.NewGuid(), 5 * 60, DateTime.UtcNow);
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.EndEarly(at, 60, BreakEndReason.Completed));

        session.EndEarly(at, 60, BreakEndReason.UserEnded);
        Assert.Equal(BreakState.EndedEarly, session.State);
        Assert.Equal(BreakEndReason.UserEnded, session.EndReason);
    }

    [Fact]
    public void Interrupt_requires_active_state_and_involuntary_reason()
    {
        var session = BreakSession.Start(Guid.NewGuid(), 5 * 60, DateTime.UtcNow);
        var at = DateTime.UtcNow;

        Assert.Throws<DomainException>(() => session.MarkInterrupted(at, BreakEndReason.UserEnded));
        Assert.Throws<DomainException>(() => session.MarkInterrupted(at, BreakEndReason.Completed));

        session.MarkInterrupted(at, BreakEndReason.LockOrSleepTimeout);
        Assert.Equal(BreakState.Interrupted, session.State);

        Assert.Throws<DomainException>(() => session.Complete(at, 5 * 60)); // not Active anymore
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(at, 10 * 60)); // still rejected: terminal
    }

    [Fact]
    public void Counters_cannot_be_negative_or_checkpointed_after_terminal()
    {
        var session = BreakSession.Start(Guid.NewGuid(), 5 * 60, DateTime.UtcNow);

        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(DateTime.UtcNow, -1));

        session.Complete(DateTime.UtcNow, 5 * 60);
        Assert.Throws<DomainException>(() => session.ApplyCheckpoint(DateTime.UtcNow, 100));
    }
}
