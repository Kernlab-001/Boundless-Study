namespace BoundlessStudy.Domain.Economy;

/// <summary>
/// Economy Rule Version 1 (`ECONOMY-1`), frozen 2026-11-12
/// (domain-business-rules.md §8). Pure, deterministic functions only.
/// Changing any frozen value requires a new version and new acceptance vectors;
/// historical ledger entries are never silently recalculated.
/// </summary>
public static class EconomyRules
{
    /// <summary>The frozen MVP economy version identifier (§8.2).</summary>
    public const string Version = "ECONOMY-1";

    /// <summary>Daily session-XP cap per completion local day (§8.4).</summary>
    public const int DailySessionXpCap = 300;

    /// <summary>Task completion XP, awarded once per task identity (§8.5).</summary>
    public const int TaskCompletionXp = 25;

    /// <summary>Session XP requires at least 10 active minutes (§8.3).</summary>
    public const int MinimumActiveSecondsForSessionXp = 600;

    /// <summary>A completed session with ≥10 active minutes qualifies the day for the streak (§8.8).</summary>
    public const int QualifiedSessionMinimumActiveSeconds = 600;

    /// <summary>XP per level (§8.7).</summary>
    public const int XpPerLevel = 500;

    /// <summary>Maximum level; total XP is still recorded above it (§8.7).</summary>
    public const int MaxLevel = 50;

    /// <summary>Fixed MVP idle threshold (§3.3).</summary>
    public const int IdleThresholdSeconds = 300;

    /// <summary>Checkpoint interval (§3.6).</summary>
    public const int CheckpointIntervalSeconds = 10;

    /// <summary>Website-protection heartbeat timeout (§7.2).</summary>
    public const int HeartbeatTimeoutSeconds = 30;

    /// <summary>App-guard grace period before the overlay appears (§7.1).</summary>
    public const int OverlayGracePeriodSeconds = 3;

    /// <summary>Profile rates per active minute (§8.3).</summary>
    public static int RateForProfile(string profileCode) => profileCode switch
    {
        Entities.BuiltInProfiles.FlexibleCode => 2,
        Entities.BuiltInProfiles.BalancedCode => 3,
        Entities.BuiltInProfiles.StrictCode => 4,
        _ => throw new DomainException("PROFILE_CODE_UNKNOWN", $"No XP rate is defined for profile '{profileCode}'."),
    };

    /// <summary>Outcome multipliers (§8.3): Completed 1.0, EndedEarly 0.5, Abandoned 0.5.</summary>
    public static decimal MultiplierForOutcome(SessionState terminalState) => terminalState switch
    {
        SessionState.Completed => 1.0m,
        SessionState.EndedEarly => 0.5m,
        SessionState.Abandoned => 0.5m,
        _ => throw new DomainException("SESSION_NOT_REWARD_TERMINAL",
            $"State {terminalState} is not a terminal reward state; no session XP is computed for it."),
    };

    /// <summary>
    /// Computes the session award before the daily cap is applied (§8.3):
    /// activeMinutes = floor(activeSeconds/60); rawXp = activeMinutes × rate × multiplier;
    /// computedXp = floor(rawXp). Returns 0 below the 10-active-minute threshold.
    /// </summary>
    public static int ComputeSessionXp(int activeStudySeconds, string profileCode, SessionState terminalState)
    {
        if (activeStudySeconds < 0)
        {
            throw new DomainException("SESSION_COUNTER_NEGATIVE", "Active-study seconds cannot be negative.");
        }

        if (activeStudySeconds < MinimumActiveSecondsForSessionXp)
        {
            return 0;
        }

        var activeMinutes = activeStudySeconds / 60; // floor
        var rawXp = activeMinutes * (decimal)RateForProfile(profileCode) * MultiplierForOutcome(terminalState);
        return (int)Math.Floor(rawXp);
    }

    /// <summary>
    /// True when a terminal session qualifies its completion local day for the streak
    /// (§8.8): only Completed with ≥10 active minutes. Ended-early and abandoned
    /// sessions never qualify, even when they earn partial XP.
    /// </summary>
    public static bool IsStreakQualified(SessionState terminalState, int activeStudySeconds) =>
        terminalState == SessionState.Completed
        && activeStudySeconds >= QualifiedSessionMinimumActiveSeconds;

    /// <summary>
    /// Applies the daily session-XP cap (§8.4): available = max(0, 300 − prior);
    /// award = min(computed, available). Cap-zero qualified sessions still qualify
    /// the streak but produce no ledger entry (Amount must be > 0).
    /// </summary>
    public static int ApplyDailySessionCap(int computedXp, int priorSessionXpOnLocalDate)
    {
        if (priorSessionXpOnLocalDate < 0)
        {
            throw new DomainException("LEDGER_PRIOR_TOTAL_INVALID", "Prior session XP for a local day cannot be negative.");
        }

        var available = Math.Max(0, DailySessionXpCap - priorSessionXpOnLocalDate);
        return Math.Min(Math.Max(0, computedXp), available);
    }

    /// <summary>Level curve (§8.7): level = min(50, floor(totalXp / 500) + 1).</summary>
    public static int LevelForTotalXp(long totalXp)
    {
        if (totalXp < 0)
        {
            throw new DomainException("LEDGER_TOTAL_INVALID", "Total XP cannot be negative.");
        }

        return (int)Math.Min(MaxLevel, (totalXp / XpPerLevel) + 1);
    }
}

