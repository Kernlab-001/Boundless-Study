namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// Append-only ledger entry for XP (conceptual-data-model.md §4.12, ECONOMY-1 §8.10).
/// Immutable: no update or delete through normal operation. Amount > 0 in MVP.
/// Every entry stores the economy rule version that generated it and is never
/// silently recalculated.
/// </summary>
public class RewardLedgerEntry
{
    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public CurrencyCode CurrencyCode { get; private set; }

    public int Amount { get; private set; }

    public LedgerSourceType SourceType { get; private set; }

    public string SourceId { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public DateTime AwardedAtUtc { get; private set; }

    public DateOnly AwardLocalDate { get; private set; }

    public int UtcOffsetMinutes { get; private set; }

    public string EconomyRuleVersion { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private RewardLedgerEntry()
    {
    }

    public static RewardLedgerEntry Create(
        Guid localProfileId,
        CurrencyCode currencyCode,
        int amount,
        LedgerSourceType sourceType,
        string sourceId,
        DateTime awardedAtUtc,
        DateOnly awardLocalDate,
        int utcOffsetMinutes,
        string economyRuleVersion,
        string? notes = null,
        DateTime? createdAtUtc = null)
    {
        if (amount <= 0)
        {
            throw new DomainException("LEDGER_AMOUNT_INVALID", "A ledger entry amount must be positive.");
        }

        var sid = (sourceId ?? string.Empty).Trim();
        if (sid.Length == 0 || sid.Length > 100)
        {
            throw new DomainException("LEDGER_SOURCE_INVALID", "A ledger source id must be 1–100 characters.");
        }

        if (string.IsNullOrWhiteSpace(economyRuleVersion))
        {
            throw new DomainException("LEDGER_RULE_VERSION_REQUIRED", "A ledger entry must record the economy rule version that generated it.");
        }

        var key = ValueObjects.IdempotencyKey.For(sourceType, sid, currencyCode);

        var note = (notes ?? string.Empty).Trim();
        if (note.Length > 500)
        {
            throw new DomainException("LEDGER_NOTES_TOO_LONG", "Ledger notes may be at most 500 characters.");
        }

        return new RewardLedgerEntry
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            CurrencyCode = currencyCode,
            Amount = amount,
            SourceType = sourceType,
            SourceId = sid,
            IdempotencyKey = key.Value,
            AwardedAtUtc = awardedAtUtc,
            AwardLocalDate = awardLocalDate,
            UtcOffsetMinutes = utcOffsetMinutes,
            EconomyRuleVersion = economyRuleVersion,
            Notes = note.Length == 0 ? null : note,
            CreatedAtUtc = createdAtUtc ?? awardedAtUtc,
        };
    }

    public bool IsXp => CurrencyCode == CurrencyCode.Xp;

    public bool IsToken => CurrencyCode == CurrencyCode.Token;
}

/// <summary>
/// An achievement definition (conceptual-data-model.md §4.13, §8.9.1 change control).
/// <see cref="Code"/> permanently identifies one eligibility definition;
/// <see cref="CriteriaType"/> and <see cref="Threshold"/> are immutable for the code.
/// Only <see cref="IsEnabled"/> may change after seeding.
/// </summary>
public class AchievementDefinition
{
    public const int MaxCodeLength = 40;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public AchievementCriteriaType CriteriaType { get; private set; }

    public int Threshold { get; private set; }

    public int XpAward { get; private set; }

    public string EconomyRuleVersion { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    private AchievementDefinition()
    {
    }

    public static AchievementDefinition Create(
        string code,
        string name,
        string description,
        AchievementCriteriaType criteriaType,
        int threshold,
        int xpAward,
        string economyRuleVersion,
        bool isEnabled = true)
    {
        code = (code ?? string.Empty).Trim();
        name = (name ?? string.Empty).Trim();
        description = (description ?? string.Empty).Trim();

        if (code.Length == 0 || code.Length > MaxCodeLength)
        {
            throw new DomainException("ACHIEVEMENT_CODE_INVALID", $"An achievement code must be 1–{MaxCodeLength} characters.");
        }

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            throw new DomainException("ACHIEVEMENT_NAME_INVALID", $"An achievement name must be 1–{MaxNameLength} characters.");
        }

        if (description.Length > MaxDescriptionLength)
        {
            throw new DomainException("ACHIEVEMENT_DESCRIPTION_TOO_LONG", $"An achievement description may be at most {MaxDescriptionLength} characters.");
        }

        if (threshold <= 0)
        {
            throw new DomainException("ACHIEVEMENT_THRESHOLD_INVALID", "An achievement threshold must be positive.");
        }

        if (xpAward <= 0)
        {
            throw new DomainException("ACHIEVEMENT_XP_INVALID", "An achievement XP award must be positive.");
        }

        if (string.IsNullOrWhiteSpace(economyRuleVersion))
        {
            throw new DomainException("ACHIEVEMENT_RULE_VERSION_REQUIRED", "An achievement definition must record the economy rule version that defines its XP award.");
        }

        return new AchievementDefinition
        {
            Code = code,
            Name = name,
            Description = description,
            CriteriaType = criteriaType,
            Threshold = threshold,
            XpAward = xpAward,
            EconomyRuleVersion = economyRuleVersion,
            IsEnabled = isEnabled,
        };
    }

    public void SetEnabled(bool enabled)
    {
        // Disabling never deletes the definition or historical unlocks (§4.13).
        IsEnabled = enabled;
    }
}

/// <summary>
/// A per-profile achievement unlock (conceptual-data-model.md §4.14). Unique
/// (LocalProfileId, AchievementCode); unlocked at most once per profile.
/// No criteria version or unlock-time snapshot is stored by design.
/// </summary>
public class UserAchievement
{
    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string AchievementCode { get; private set; } = string.Empty;

    public DateTime UnlockedAtUtc { get; private set; }

    public Guid? LedgerEntryId { get; private set; }

    public DateTime? SeenAtUtc { get; private set; }

    private UserAchievement()
    {
    }

    public static UserAchievement Create(
        Guid localProfileId,
        string achievementCode,
        DateTime unlockedAtUtc,
        Guid? ledgerEntryId = null)
    {
        var code = (achievementCode ?? string.Empty).Trim();
        if (code.Length == 0 || code.Length > AchievementDefinition.MaxCodeLength)
        {
            throw new DomainException("ACHIEVEMENT_CODE_INVALID", "A user achievement must reference a valid achievement code.");
        }

        return new UserAchievement
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            AchievementCode = code,
            UnlockedAtUtc = unlockedAtUtc,
            LedgerEntryId = ledgerEntryId,
        };
    }

    /// <summary>Links the one-time XP award. Idempotent for the same entry.</summary>
    public void AttachLedgerEntry(Guid ledgerEntryId)
    {
        if (LedgerEntryId is not null && LedgerEntryId != ledgerEntryId)
        {
            throw new DomainException("ACHIEVEMENT_LEDGER_ALREADY_SET", "A ledger entry is already linked to this unlock.");
        }

        LedgerEntryId = ledgerEntryId;
    }

    public void MarkSeen(DateTime seenAtUtc)
    {
        SeenAtUtc = seenAtUtc;
    }
}
