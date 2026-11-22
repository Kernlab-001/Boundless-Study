namespace BoundlessStudy.Application.Abstractions;

/// <summary>
/// The agent's clock port. UtcNow, the local offset at that instant, and the local
/// calendar date derived with the recorded offset (domain-business-rules.md §10).
/// Infrastructure provides the real implementation.
/// </summary>
public interface ILocalClock
{
    DateTime UtcNow { get; }

    /// <summary>The user's local UTC offset in minutes at UtcNow (e.g. +330 for UTC+5:30).</summary>
    int UtcOffsetMinutes { get; }

    /// <summary>The local calendar date at UtcNow.</summary>
    DateOnly ToLocalDate(DateTime utcTime);
}

/// <summary>
/// Transaction boundary port (§8.11). The terminal transition, its session-XP entry,
/// any achievement unlocks, the streak projection, and cached totals commit in one
/// transaction. Implementations must guarantee that a crash before CommitAsync
/// leaves no terminal state, no entry, and no cache update.
/// </summary>
public interface IUnitOfWork
{
    Task BeginAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Derived reward-state port (conceptual-data-model.md §5). Returns values computed
/// from primary data inside the current transaction so award math sees uncommitted
/// changes from this same use case. Implementations derive counts from sessions,
/// ledger, tasks, and events — never from caches alone.
/// </summary>
public interface IRewardStateProvider
{
    /// <summary>Completed sessions with ≥10 active minutes (qualified sessions), lifetime.</summary>
    Task<int> GetQualifiedSessionCountAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    /// <summary>Completed sessions on the Strict profile with ≥10 active minutes (§8.9 ACH-004).</summary>
    Task<int> GetStrictQualifiedSessionCountAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accumulated active-study minutes across every terminal focus session
    /// (Completed, EndedEarly, Abandoned), §8.9 ACH-002. ACH-002 says “Accumulate
    /// 60 verified active-study minutes” without a completion restriction; only the
    /// recorded ActiveStudySeconds metric counts, never wall-clock elapsed time.
    /// Non-terminal sessions are excluded until their terminal reward event.
    /// </summary>
    Task<int> GetTotalActiveStudyMinutesAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count of tasks that have been completed at least once. Reopening, archiving,
    /// or soft-deleting a completed task keeps counting the historical completion.
    /// </summary>
    Task<int> GetCompletedTaskCountAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Qualified local dates for the streak: the completion local date of every
    /// completed session with ≥10 active minutes (§8.8). Derives from sessions and
    /// completion events, not the ledger, so cap-zero qualified sessions still count.
    /// </summary>
    Task<IReadOnlyList<DateOnly>> ListQualifiedLocalDatesAsync(Guid localProfileId, CancellationToken cancellationToken = default);
}
