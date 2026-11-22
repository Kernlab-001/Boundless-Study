namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// One local study profile per Windows user (conceptual-data-model.md §4.1).
/// MVP creates exactly one profile; multi-profile support is not included.
/// Cached totals are rebuildable from the ledger and never the only copy.
/// </summary>
public class LocalProfile
{
    public const int MaxDisplayNameLength = 80;

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? LastOpenedAtUtc { get; private set; }

    public long CachedTotalXp { get; private set; }

    public int CachedLevel { get; private set; }

    public string EconomyRuleVersion { get; private set; }

    public bool IsOnboarded { get; private set; }

    public InstallationMode InstallationMode { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private LocalProfile()
    {
        EconomyRuleVersion = Economy.EconomyRules.Version;
    }

    /// <summary>Creates the single MVP local profile.</summary>
    public static LocalProfile Create(DateTime createdAtUtc, string? displayName = null)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = "My study profile";
        }

        if (name.Length > MaxDisplayNameLength)
        {
            throw new DomainException("PROFILE_NAME_TOO_LONG", $"The profile display name may be at most {MaxDisplayNameLength} characters.");
        }

        return new LocalProfile
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            CachedLevel = 1,
            EconomyRuleVersion = Economy.EconomyRules.Version,
            InstallationMode = InstallationMode.Limited,
        };
    }

    /// <summary>Rehydrates a persisted profile without re-running creation rules.</summary>
    public static LocalProfile Rehydrate(
        Guid id,
        string displayName,
        DateTime createdAtUtc,
        DateTime? lastOpenedAtUtc,
        long cachedTotalXp,
        int cachedLevel,
        string economyRuleVersion,
        bool isOnboarded,
        InstallationMode installationMode,
        DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("PROFILE_NAME_REQUIRED", "A persisted profile must have a display name.");
        }

        if (cachedTotalXp < 0)
        {
            throw new DomainException("PROFILE_TOTAL_XP_NEGATIVE", "Cached total XP cannot be negative.");
        }

        if (cachedLevel < 1)
        {
            throw new DomainException("PROFILE_LEVEL_INVALID", "Cached level must be at least 1.");
        }

        return new LocalProfile
        {
            Id = id,
            DisplayName = displayName,
            CreatedAtUtc = createdAtUtc,
            LastOpenedAtUtc = lastOpenedAtUtc,
            CachedTotalXp = cachedTotalXp,
            CachedLevel = cachedLevel,
            EconomyRuleVersion = economyRuleVersion,
            IsOnboarded = isOnboarded,
            InstallationMode = installationMode,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    public void MarkOnboarded(DateTime atUtc)
    {
        IsOnboarded = true;
        Touch(atUtc);
    }

    public void SetInstallationMode(InstallationMode mode, DateTime atUtc)
    {
        InstallationMode = mode;
        Touch(atUtc);
    }

    public void MarkOpened(DateTime atUtc)
    {
        LastOpenedAtUtc = atUtc;
        Touch(atUtc);
    }

    /// <summary>
    /// Updates the rebuildable cache in the same transaction as the source change
    /// (conceptual-data-model.md §5). Never becomes the only copy of a reward.
    /// </summary>
    public void SetCachedRewardTotals(long totalXp, int level, DateTime atUtc)
    {
        if (totalXp < 0)
        {
            throw new DomainException("PROFILE_TOTAL_XP_NEGATIVE", "Cached total XP cannot be negative.");
        }

        if (level < 1)
        {
            throw new DomainException("PROFILE_LEVEL_INVALID", "Cached level must be at least 1.");
        }

        CachedTotalXp = totalXp;
        CachedLevel = level;
        Touch(atUtc);
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
