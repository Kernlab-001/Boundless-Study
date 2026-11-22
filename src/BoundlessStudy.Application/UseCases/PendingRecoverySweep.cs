using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Host-agnostic sweep that abandons a pending <c>RecoverableInterrupted</c> focus
/// session once its approved 24-hour recovery window has elapsed (A6, approved
/// 2026-11-19).
/// <para>
/// Expiry is currently enforced lazily: <see cref="StartFocusSessionHandler"/> and
/// <see cref="RecoverFocusSessionHandler"/> check the window only when the user
/// starts or resumes a session. If the user never returns, an unresolved
/// interruption would stay pending forever. This type is the reusable
/// background-compatible complement. The per-user Agent will call
/// <see cref="SweepAsync"/> periodically once the UI pipe foundation exists; this
/// type deliberately owns no timer, thread, <c>Task.Run</c>, or Agent/UI
/// dependency, so the same logic is exercised in tests without a process host.
/// Nothing schedules it yet.
/// </para>
/// <para>
/// Boundary: the interruption anchor is
/// <see cref="FocusSession.EffectiveInterruptionAnchorUtc"/> (the dedicated
/// <c>InterruptedAtUtc</c> value, falling back to <c>UpdatedAtUtc</c> for legacy
/// rows). A pending session is abandoned when <c>now - anchor &gt;= 24:00:00</c>,
/// inclusive. At 23:59:59 it is not expired and remains resumable; at exactly
/// 24:00:00 it is expired; at 24:00:01 it is expired. The comparison lives in
/// <see cref="FocusSession.IsPendingInterruptionExpired"/> and is not reimplemented
/// in this class.
/// </para>
/// <para>
/// The transition always runs through <see cref="AbandonFocusSessionHandler"/> with
/// <see cref="FocusEndReason.Unknown"/>, so the terminal event, the partial reward,
/// achievement evaluation, and cached totals commit in the same transaction as the
/// state change. Terminal sessions are never touched, and the handler's idempotency
/// keyed by session identity means a repeated sweep cannot duplicate a reward or an
/// event. Only <c>RecoverableInterrupted</c> qualifies: Active, Blocked,
/// SystemPaused, and terminal sessions are ignored.
/// </para>
/// </summary>
public sealed class PendingRecoverySweep
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly AbandonFocusSessionHandler _abandon;
    private readonly ILocalClock _clock;

    public PendingRecoverySweep(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        AbandonFocusSessionHandler abandon,
        ILocalClock clock)
    {
        _profiles = profiles;
        _sessions = sessions;
        _abandon = abandon;
        _clock = clock;
    }

    /// <summary>
    /// Factory creating a sweep instance wired with all domain reward and event recording dependencies.
    /// </summary>
    public static PendingRecoverySweep Create(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        IUnitOfWork unitOfWork,
        IRewardLedgerRepository ledger,
        IAchievementRepository achievements,
        IUserAchievementRepository userAchievements,
        IRewardStateProvider rewardState,
        ISessionEventRepository events,
        ILocalClock clock)
    {
        var rewards = new RewardService(ledger, achievements, userAchievements, rewardState, clock);
        var recorder = new SessionEventRecorder(events, clock);
        var recover = new RecoverFocusSessionHandler(sessions, profiles, unitOfWork, rewards, clock, recorder);
        var abandon = new AbandonFocusSessionHandler(sessions, clock, recover);
        return new PendingRecoverySweep(profiles, sessions, abandon, clock);
    }

    /// <summary>
    /// Runs one sweep pass for the single local profile. Returns zero when no
    /// profile or no eligible pending session exists, or when a concurrent
    /// actor already resolved the session. A missing profile or session is a
    /// no-op; unexpected persistence exceptions are not caught and propagate to
    /// the host so it can log and retry the next pass.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep; cancellation propagates.</param>
    public async Task<PendingRecoverySweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _profiles.FindSingleAsync(cancellationToken);
        if (profile is null)
        {
            return new PendingRecoverySweepResult(0, 0, Array.Empty<Guid>());
        }

        var abandonedSessionIds = new List<Guid>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var session = await _sessions.FindActiveByProfileAsync(profile.Id, cancellationToken);
            if (session is not { State: SessionState.RecoverableInterrupted })
            {
                break;
            }

            if (!session.IsPendingInterruptionExpired(_clock.UtcNow))
            {
                break;
            }

            var outcome = await _abandon.HandleAsync(session.Id, FocusEndReason.Unknown, cancellationToken);
            if (!outcome.Success || !outcome.Changed)
            {
                // The handler reports failure for a profile/session that disappeared
                // between the reads above, and success-with-no-change when another
                // actor already made this session terminal. Neither leaves work for
                // this pass, and neither may create a second reward path.
                break;
            }

            abandonedSessionIds.Add(session.Id);
        }

        return new PendingRecoverySweepResult(
            1,
            abandonedSessionIds.Count,
            abandonedSessionIds.Count == 0 ? Array.Empty<Guid>() : abandonedSessionIds);
    }
}

/// <summary>Outcome of one <see cref="PendingRecoverySweep"/> pass.</summary>
/// <param name="ProfilesScanned">
/// Number of local profiles inspected: 1 when the single local profile exists,
/// 0 when none exists.
/// </param>
/// <param name="SessionsAbandoned">
/// Sessions this pass moved from RecoverableInterrupted to Abandoned: 0 or 1.
/// Already-terminal and non-interrupted sessions do not count.
/// </param>
/// <param name="AbandonedSessionIds">
/// Ids abandoned by this pass, in order; empty when nothing changed.
/// </param>
public sealed record PendingRecoverySweepResult(
    int ProfilesScanned,
    int SessionsAbandoned,
    IReadOnlyList<Guid> AbandonedSessionIds);
