using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Progress snapshot (derived values only — never edited directly; §5, §8.7, §8.8).
/// Cached totals may be shown, but the authoritative values here derive from the
/// ledger, sessions, and tasks inside the current transaction.
/// </summary>
public sealed record ProfileProgress(
    long TotalXp,
    int Level,
    int CurrentStreakDays,
    int LongestStreakDays,
    int QualifiedSessionCount,
    int StrictQualifiedSessionCount,
    int CompletedTaskCount,
    DateOnly TodayLocal,
    int SessionXpToday,
    int SessionCapRemainingToday)
{
    public int DailySessionXpCap => EconomyRules.DailySessionXpCap;
}

/// <summary>Returns the profile's derived progress for the dashboard (DSH, INS).</summary>
public sealed class GetProfileProgressHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IRewardLedgerRepository _ledger;
    private readonly IRewardStateProvider _rewardState;
    private readonly ILocalClock _clock;

    public GetProfileProgressHandler(
        ILocalProfileRepository profiles,
        IRewardLedgerRepository ledger,
        IRewardStateProvider rewardState,
        ILocalClock clock)
    {
        _profiles = profiles;
        _ledger = ledger;
        _rewardState = rewardState;
        _clock = clock;
    }

    public async Task<ProfileProgress?> HandleAsync(Guid localProfileId, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return null;
        }

        var today = _clock.ToLocalDate(_clock.UtcNow);
        var totalXp = await _ledger.SumXpAsync(localProfileId, cancellationToken);
        var qualifiedDates = await _rewardState.ListQualifiedLocalDatesAsync(localProfileId, cancellationToken);
        var sessionXpToday = await _ledger.SumSessionXpForLocalDateAsync(localProfileId, today, cancellationToken);

        return new ProfileProgress(
            totalXp,
            EconomyRules.LevelForTotalXp(totalXp),
            StreakCalculator.CurrentStreak(qualifiedDates, today),
            StreakCalculator.LongestStreak(qualifiedDates),
            await _rewardState.GetQualifiedSessionCountAsync(localProfileId, cancellationToken),
            await _rewardState.GetStrictQualifiedSessionCountAsync(localProfileId, cancellationToken),
            await _rewardState.GetCompletedTaskCountAsync(localProfileId, cancellationToken),
            today,
            sessionXpToday,
            Math.Max(0, EconomyRules.DailySessionXpCap - sessionXpToday));
    }
}
