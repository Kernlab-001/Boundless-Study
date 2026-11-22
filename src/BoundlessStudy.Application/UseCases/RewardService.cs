using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Domain.ValueObjects;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Shared idempotent reward plumbing for `ECONOMY-1` (§8.10, §8.11). Every award
/// looks up its idempotency key first; a duplicate request is success-with-no-change.
/// Achievement unlocks for one source event commit in one transaction, ascending
/// by achievement code, and never partially.
/// </summary>
public sealed class RewardService
{
    private readonly IRewardLedgerRepository _ledger;
    private readonly IAchievementRepository _achievements;
    private readonly IUserAchievementRepository _userAchievements;
    private readonly IRewardStateProvider _rewardState;
    private readonly ILocalClock _clock;

    public RewardService(
        IRewardLedgerRepository ledger,
        IAchievementRepository achievements,
        IUserAchievementRepository userAchievements,
        IRewardStateProvider rewardState,
        ILocalClock clock)
    {
        _ledger = ledger;
        _achievements = achievements;
        _userAchievements = userAchievements;
        _rewardState = rewardState;
        _clock = clock;
    }

    /// <summary>
    /// Returns the existing entry when the idempotency key is present (success-with-
    /// no-change); otherwise creates the entry. Returns null, null only when the
    /// computed amount is 0 (cap-zero qualified sessions create no ledger entry).
    /// </summary>
    public async Task<(RewardLedgerEntry? Entry, bool AlreadyExisted)> AwardAsync(
        Guid localProfileId,
        CurrencyCode currency,
        int amount,
        LedgerSourceType sourceType,
        string sourceId,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            return (null, false); // No entry: Amount must be > 0 (§4.12).
        }

        var key = IdempotencyKey.For(sourceType, sourceId, currency).Value;
        var existing = await _ledger.FindByIdempotencyKeyAsync(key, cancellationToken);
        if (existing is not null)
        {
            return (existing, true);
        }

        var now = _clock.UtcNow;
        var entry = RewardLedgerEntry.Create(
            localProfileId,
            currency,
            amount,
            sourceType,
            sourceId,
            now,
            _clock.ToLocalDate(now),
            _clock.UtcOffsetMinutes,
            EconomyRules.Version,
            notes);
        await _ledger.AddAsync(entry, cancellationToken);
        return (entry, false);
    }

    /// <summary>
    /// Evaluates achievements against the current progress and persists each new
    /// unlock with its ledger entry, in ascending code order (§8.6). Returns the
    /// newly unlocked codes. Already-unlocked codes are skipped (duplicate requests
    /// return success-with-no-change).
    /// </summary>
    public async Task<IReadOnlyList<string>> EvaluateAndUnlockAchievementsAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        var progress = new AchievementProgress(
            await _rewardState.GetQualifiedSessionCountAsync(localProfileId, cancellationToken),
            await _rewardState.GetTotalActiveStudyMinutesAsync(localProfileId, cancellationToken),
            0, // streak handled by the caller when streak-bearing achievements must be evaluated atomically
            await _rewardState.GetStrictQualifiedSessionCountAsync(localProfileId, cancellationToken),
            await _rewardState.GetCompletedTaskCountAsync(localProfileId, cancellationToken));

        var currentStreak = StreakCalculator.CurrentStreak(
            await _rewardState.ListQualifiedLocalDatesAsync(localProfileId, cancellationToken),
            _clock.ToLocalDate(_clock.UtcNow));
        progress = progress with { CurrentStreakDays = currentStreak };

        var definitions = await _achievements.ListAsync(cancellationToken);
        var unlockedCodes = (await _userAchievements.ListByProfileAsync(localProfileId, cancellationToken))
            .Select(a => a.AchievementCode)
            .ToArray();
        var newCodes = AchievementEvaluator.EvaluateNewUnlocks(definitions, unlockedCodes, progress);

        var newlyUnlocked = new List<string>();
        foreach (var code in newCodes)
        {
            var definition = definitions.Single(d => d.Code == code);
            var unlock = UserAchievement.Create(localProfileId, code, _clock.UtcNow);
            var (entry, _) = await AwardAsync(
                localProfileId,
                CurrencyCode.Xp,
                definition.XpAward,
                LedgerSourceType.AchievementUnlock,
                code,
                notes: $"Achievement unlock: {definition.Name}",
                cancellationToken);
            if (entry is not null)
            {
                unlock.AttachLedgerEntry(entry.Id);
            }

            await _userAchievements.AddAsync(unlock, cancellationToken);
            newlyUnlocked.Add(code);
        }

        return newlyUnlocked;
    }

    /// <summary>Sum of XP ledger entries for the profile — the derived total (§5, §8.10).</summary>
    public Task<long> TotalXpAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        _ledger.SumXpAsync(localProfileId, cancellationToken);

    /// <summary>Idempotent-retry lookup by the exact ledger key (§8.10).</summary>
    public Task<RewardLedgerEntry?> FindEntryAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        _ledger.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

    /// <summary>Computes the cap-reduced session award for a terminal session (§8.3–8.4).</summary>
    public async Task<int> ComputeSessionAwardAsync(
        FocusSession session,
        CancellationToken cancellationToken = default)
    {
        var computed = EconomyRules.ComputeSessionXp(session.ActiveStudySeconds, session.ProfileCode, session.State);
        if (computed == 0)
        {
            return 0;
        }

        var completionLocalDate = _clock.ToLocalDate(_clock.UtcNow);
        var prior = await _ledger.SumSessionXpForLocalDateAsync(session.LocalProfileId, completionLocalDate, cancellationToken);
        return EconomyRules.ApplyDailySessionCap(computed, prior);
    }
}
