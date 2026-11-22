namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// Seeded reference data for the three MVP focus profiles
/// (conceptual-data-model.md §4.4, domain-business-rules.md §4.1).
/// </summary>
public class FocusProfile
{
    public const int MaxCodeLength = 20;
    public const int MaxDisplayNameLength = 40;

    public string Code { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public EarlyExitMode EarlyExitMode { get; private set; }

    public int EarlyExitCooldownSeconds { get; private set; }

    public int MaxExceptionsPerSession { get; private set; }

    public int ExceptionDurationSeconds { get; private set; }

    public int ExceptionDelaySeconds { get; private set; }

    public int XpPerActiveMinute { get; private set; }

    public bool RequiresBrowserWhenSiteRulesExist { get; private set; }

    public bool IsEnabled { get; private set; }

    public string EconomyRuleVersion { get; private set; }

    private FocusProfile()
    {
        EconomyRuleVersion = Economy.EconomyRules.Version;
    }

    public static FocusProfile Create(
        string code,
        string displayName,
        EarlyExitMode earlyExitMode,
        int earlyExitCooldownSeconds,
        int maxExceptionsPerSession,
        int exceptionDurationSeconds,
        int exceptionDelaySeconds,
        int xpPerActiveMinute,
        bool requiresBrowserWhenSiteRulesExist,
        bool isEnabled,
        string economyRuleVersion)
    {
        code = (code ?? string.Empty).Trim();
        displayName = (displayName ?? string.Empty).Trim();

        if (code.Length == 0 || code.Length > MaxCodeLength)
        {
            throw new DomainException("PROFILE_CODE_INVALID", $"A profile code must be 1–{MaxCodeLength} characters.");
        }

        if (displayName.Length == 0 || displayName.Length > MaxDisplayNameLength)
        {
            throw new DomainException("PROFILE_DISPLAY_NAME_INVALID", $"A profile display name must be 1–{MaxDisplayNameLength} characters.");
        }

        if (earlyExitCooldownSeconds < 0 || maxExceptionsPerSession < 0 || exceptionDurationSeconds < 0 || exceptionDelaySeconds < 0)
        {
            throw new DomainException("PROFILE_VALUES_NEGATIVE", "Profile timing and count values cannot be negative.");
        }

        if (xpPerActiveMinute <= 0)
        {
            throw new DomainException("PROFILE_RATE_INVALID", "XP per active minute must be positive.");
        }

        if (string.IsNullOrWhiteSpace(economyRuleVersion))
        {
            throw new DomainException("PROFILE_ECONOMY_VERSION_REQUIRED", "A profile must record its economy rule version.");
        }

        return new FocusProfile
        {
            Code = code,
            DisplayName = displayName,
            EarlyExitMode = earlyExitMode,
            EarlyExitCooldownSeconds = earlyExitCooldownSeconds,
            MaxExceptionsPerSession = maxExceptionsPerSession,
            ExceptionDurationSeconds = exceptionDurationSeconds,
            ExceptionDelaySeconds = exceptionDelaySeconds,
            XpPerActiveMinute = xpPerActiveMinute,
            RequiresBrowserWhenSiteRulesExist = requiresBrowserWhenSiteRulesExist,
            IsEnabled = isEnabled,
            EconomyRuleVersion = economyRuleVersion,
        };
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
    }
}

/// <summary>
/// The three approved MVP built-in profiles with their exact ECONOMY-1 values
/// (domain-business-rules.md §4.1 table). Frozen seed data; not user-editable in MVP.
/// </summary>
public static class BuiltInProfiles
{
    public const string FlexibleCode = "Flexible";
    public const string BalancedCode = "Balanced";
    public const string StrictCode = "Strict";

    public static IReadOnlyList<FocusProfile> Seed() =>
    [
        Flexible(),
        Balanced(),
        Strict(),
    ];

    public static FocusProfile Flexible() => FocusProfile.Create(
        FlexibleCode,
        "Flexible",
        EarlyExitMode.Immediate,
        earlyExitCooldownSeconds: 0,
        maxExceptionsPerSession: 0,
        exceptionDurationSeconds: 0,
        exceptionDelaySeconds: 0,
        xpPerActiveMinute: 2,
        requiresBrowserWhenSiteRulesExist: false,
        isEnabled: true,
        economyRuleVersion: Economy.EconomyRules.Version);

    public static FocusProfile Balanced() => FocusProfile.Create(
        BalancedCode,
        "Balanced",
        EarlyExitMode.ConfirmationCooldown,
        earlyExitCooldownSeconds: 10,
        maxExceptionsPerSession: 2,
        exceptionDurationSeconds: 300,
        exceptionDelaySeconds: 0,
        xpPerActiveMinute: 3,
        requiresBrowserWhenSiteRulesExist: false,
        isEnabled: true,
        economyRuleVersion: Economy.EconomyRules.Version);

    public static FocusProfile Strict() => FocusProfile.Create(
        StrictCode,
        "Strict",
        EarlyExitMode.TypedConfirmationCooldown,
        earlyExitCooldownSeconds: 30,
        maxExceptionsPerSession: 1,
        exceptionDurationSeconds: 120,
        exceptionDelaySeconds: 30,
        xpPerActiveMinute: 4,
        requiresBrowserWhenSiteRulesExist: true,
        isEnabled: true,
        economyRuleVersion: Economy.EconomyRules.Version);
}
