namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// A separate timed rest block (conceptual-data-model.md §4.6, domain-business-rules.md §6).
/// Earns no XP, creates no ledger entry, and never changes the streak.
/// </summary>
public class BreakSession
{
    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public Guid? FocusSessionId { get; private set; }

    public int PlannedSeconds { get; private set; }

    public BreakState State { get; private set; } = BreakState.Active;

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? EndedAtUtc { get; private set; }

    public int ElapsedSeconds { get; private set; }

    public BreakEndReason? EndReason { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private BreakSession()
    {
    }

    public static BreakSession Start(
        Guid localProfileId,
        int plannedSeconds,
        DateTime startedAtUtc,
        Guid? precedingFocusSessionId = null)
    {
        ValueObjects.PlannedBreakSeconds.Create(plannedSeconds);

        return new BreakSession
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            FocusSessionId = precedingFocusSessionId,
            PlannedSeconds = plannedSeconds,
            State = BreakState.Active,
            StartedAtUtc = startedAtUtc,
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
    }

    public void ApplyCheckpoint(DateTime checkpointAtUtc, int elapsedSeconds)
    {
        if (State is BreakState.Completed or BreakState.EndedEarly or BreakState.Interrupted)
        {
            throw new DomainException("BREAK_TERMINAL", "A terminal break cannot be checkpointed.");
        }

        if (elapsedSeconds < 0)
        {
            throw new DomainException("BREAK_COUNTER_NEGATIVE", "Break elapsed time cannot be negative.");
        }

        ElapsedSeconds = elapsedSeconds;
        Touch(checkpointAtUtc);
    }

    /// <summary>Records lock/sleep downtime without ending the break.</summary>
    public void MarkInterrupted(DateTime atUtc, BreakEndReason interruptionReason)
    {
        if (State is not BreakState.Active)
        {
            throw new DomainException("BREAK_TRANSITION_INVALID", $"A break in state {State} cannot be interrupted.");
        }

        if (interruptionReason is BreakEndReason.Completed or BreakEndReason.UserEnded)
        {
            throw new DomainException("BREAK_INTERRUPT_REASON_INVALID", "A break interruption must record an involuntary reason.");
        }

        State = BreakState.Interrupted;
        EndReason = interruptionReason;
        Touch(atUtc);
    }

    public void Complete(DateTime completedAtUtc, int elapsedSeconds)
    {
        EnsureCanFinish(nameof(Complete));
        if (elapsedSeconds < 0)
        {
            throw new DomainException("BREAK_COUNTER_NEGATIVE", "Break elapsed time cannot be negative.");
        }

        ElapsedSeconds = elapsedSeconds;
        State = BreakState.Completed;
        EndReason = BreakEndReason.Completed;
        EndedAtUtc = completedAtUtc;
        Touch(completedAtUtc);
    }

    public void EndEarly(DateTime endedAtUtc, int elapsedSeconds, BreakEndReason endReason)
    {
        EnsureCanFinish(nameof(EndEarly));
        if (endReason is BreakEndReason.Completed)
        {
            throw new DomainException("BREAK_END_REASON_INVALID", "A break ended early must record a reason other than Completed.");
        }

        if (elapsedSeconds < 0)
        {
            throw new DomainException("BREAK_COUNTER_NEGATIVE", "Break elapsed time cannot be negative.");
        }

        ElapsedSeconds = elapsedSeconds;
        State = BreakState.EndedEarly;
        EndReason = endReason;
        EndedAtUtc = endedAtUtc;
        Touch(endedAtUtc);
    }

    public bool IsTerminal =>
        State is BreakState.Completed or BreakState.EndedEarly or BreakState.Interrupted;

    public bool IsActive => State is BreakState.Active;

    private void EnsureCanFinish(string operation)
    {
        if (State is not BreakState.Active)
        {
            throw new DomainException("BREAK_TRANSITION_INVALID", $"{operation} requires the Active state, but the break is {State}.");
        }
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
