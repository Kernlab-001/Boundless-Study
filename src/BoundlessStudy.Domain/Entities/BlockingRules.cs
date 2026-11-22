namespace BoundlessStudy.Domain.Entities;

using System.Collections.Frozen;

/// <summary>
/// A blocked application rule (conceptual-data-model.md §4.9, domain-business-rules.md §7.1).
/// System-critical and product-owned targets are rejected at construction
/// (§11: no rule may target the agent, UI, service, native bridge, or required system process).
/// </summary>
public class BlockedApp
{
    public const int MaxDisplayNameLength = 200;

    /// <summary>
    /// Lowercase executable names that must never be blocked: product components,
    /// required system-critical processes, and Windows accessibility tools (domain-business-rules.md §7.1, §11).
    /// The approved baseline is 24 targets and is grow-only (A3, approved 2026-11-19; the three missing
    /// accessibility targets VoiceAccess.exe, LiveCaptions.exe, and AtBroker.exe approved by user 2026-11-22).
    /// The frozen set is immutable at runtime.
    /// </summary>
    public static readonly FrozenSet<string> ExcludedExecutableNames = new[]
    {
        // Product components (8 entries: .exe and bare stem).
        "boundlessstudy.agent.exe",
        "boundlessstudy.agent",
        "boundlessstudy.desktop.exe",
        "boundlessstudy.desktop",
        "boundlessstudy.service.exe",
        "boundlessstudy.service",
        "boundlessstudy.bridge.exe",
        "boundlessstudy.bridge",
        // Core Windows system processes (10 entries).
        "csrss.exe",
        "dwm.exe",
        "explorer.exe",
        "lsass.exe",
        "services.exe",
        "smss.exe",
        "svchost.exe",
        "system",
        "wininit.exe",
        "winlogon.exe",
        // Accessibility tools and infrastructure (6 entries).
        "narrator.exe",
        "magnify.exe",
        "osk.exe",
        "voiceaccess.exe",
        "livecaptions.exe",
        "atbroker.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string? NormalizedExecutablePath { get; private set; }

    public string NormalizedExecutableName { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private BlockedApp()
    {
    }

    /// <summary>
    /// True when either the supplied executable name or the file name in the supplied
    /// path is a protected target. The check is case-insensitive, tolerates surrounding
    /// quotes/whitespace, handles bare stems and .exe extensions symmetrically, and handles
    /// Windows' trailing dot/space normalization. Safe for null or malformed targets.
    /// The blocking engine must call this at match time, not rely only on rule creation (A3).
    /// </summary>
    public static bool IsProtectedTarget(string? executablePath, string? executableName)
    {
        return IsExcluded(executableName) || IsExcluded(executablePath);
    }

    private static bool IsExcluded(string? target)
    {
        var name = NormalizeTargetName(target);
        if (name is null)
        {
            return false;
        }

        if (ExcludedExecutableNames.Contains(name))
        {
            return true;
        }

        // Windows process inspection (e.g. Process.ProcessName) often reports names without
        // the .exe extension, while rules or file paths may include it. If the candidate has
        // no dot extension, also check candidate + ".exe".
        if (!name.Contains('.', StringComparison.Ordinal) && ExcludedExecutableNames.Contains(name + ".exe"))
        {
            return true;
        }

        // Conversely, if the candidate has an .exe extension, also check the extensionless stem (e.g. "system.exe" -> "system").
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && ExcludedExecutableNames.Contains(name[..^4]))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runtime guard for the future blocking engine: throws instead of blocking a
    /// protected target even if a rule bypassed creation-time validation.
    /// </summary>
    public static void EnsureTargetCanBeBlocked(string? executablePath, string? executableName)
    {
        if (IsProtectedTarget(executablePath, executableName))
        {
            throw new DomainException("BLOCK_TARGET_PROTECTED", "System-critical processes, accessibility tools, and product components cannot be blocked.");
        }
    }

    private static string? NormalizeTargetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim().Trim('"').Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        // Win32 strips trailing dots and spaces from the final path component, so
        // "svchost.exe." and "svchost.exe " resolve to "svchost.exe" on Windows.
        var file = candidate.Split('/', '\\').Last().Trim().Trim('"').Trim().TrimEnd(' ', '.');
        return file.Length == 0 ? null : file;
    }

    public static BlockedApp Create(
        Guid localProfileId,
        string displayName,
        string? executablePath,
        string? executableName,
        DateTime createdAtUtc)
    {
        var target = ValueObjects.ExecutableTarget.Create(executablePath, executableName);

        EnsureTargetCanBeBlocked(target.NormalizedExecutablePath, target.NormalizedExecutableName);

        var label = (displayName ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            label = target.NormalizedExecutableName;
        }

        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        return new BlockedApp
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            DisplayName = label,
            NormalizedExecutablePath = target.NormalizedExecutablePath,
            NormalizedExecutableName = target.NormalizedExecutableName,
            IsEnabled = true,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void SetEnabled(bool enabled, DateTime atUtc)
    {
        IsEnabled = enabled;
        Touch(atUtc);
    }

    public void Rename(string displayName, DateTime atUtc)
    {
        var label = (displayName ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            throw new DomainException("BLOCK_LABEL_REQUIRED", "A display name is required.");
        }

        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        DisplayName = label;
        Touch(atUtc);
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}

/// <summary>
/// A blocked website rule (conceptual-data-model.md §4.10, domain-business-rules.md §7.2).
/// Domain is normalized (lowercase, no scheme/path, www. stripped) and unique per profile.
/// </summary>
public class BlockedSite
{
    public const int MaxDisplayNameLength = 253;

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string NormalizedDomain { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private BlockedSite()
    {
    }

    public static BlockedSite Create(Guid localProfileId, string domain, string? displayName, DateTime createdAtUtc)
    {
        var normalized = ValueObjects.NormalizedDomain.Create(domain);

        var label = (displayName ?? string.Empty).Trim();
        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        return new BlockedSite
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            NormalizedDomain = normalized.Value,
            DisplayName = label.Length == 0 ? null : label,
            IsEnabled = true,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void SetEnabled(bool enabled, DateTime atUtc)
    {
        IsEnabled = enabled;
        Touch(atUtc);
    }

    public void Rename(string? displayName, DateTime atUtc)
    {
        var label = (displayName ?? string.Empty).Trim();
        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        DisplayName = label.Length == 0 ? null : label;
        Touch(atUtc);
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}

/// <summary>
/// An allowed website rule (conceptual-data-model.md §4.11). Allowed sites take
/// precedence over blocked sites; a domain may exist in both lists and the UI
/// explains the precedence rather than silently deduplicating.
/// </summary>
public class AllowedSite
{
    public const int MaxDisplayNameLength = 253;

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string NormalizedDomain { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private AllowedSite()
    {
    }

    public static AllowedSite Create(Guid localProfileId, string domain, string? displayName, DateTime createdAtUtc)
    {
        var normalized = ValueObjects.NormalizedDomain.Create(domain);

        var label = (displayName ?? string.Empty).Trim();
        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        return new AllowedSite
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            NormalizedDomain = normalized.Value,
            DisplayName = label.Length == 0 ? null : label,
            IsEnabled = true,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void SetEnabled(bool enabled, DateTime atUtc)
    {
        IsEnabled = enabled;
        Touch(atUtc);
    }

    public void Rename(string? displayName, DateTime atUtc)
    {
        var label = (displayName ?? string.Empty).Trim();
        if (label.Length > MaxDisplayNameLength)
        {
            throw new DomainException("BLOCK_LABEL_TOO_LONG", $"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        DisplayName = label.Length == 0 ? null : label;
        Touch(atUtc);
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
