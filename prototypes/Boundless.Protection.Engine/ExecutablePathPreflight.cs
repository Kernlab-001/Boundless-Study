using System.Text.RegularExpressions;

namespace Boundless.Protection.Engine;

/// <summary>
/// A conservative, platform-independent syntax preflight for Windows executable paths.
/// It does not inspect the filesystem and never establishes executable identity.
/// </summary>
public enum ExecutablePathPreflightStatus
{
    LexicallyValidButUnverified,
    Invalid,
    UnsupportedNamespace,
    UnsupportedRoot,
    AmbiguousAlias
}

public sealed record ExecutablePathPreflightResult(
    ExecutablePathPreflightStatus Status,
    string ReasonCode)
{
    /// <summary>Even a valid lexical path is not safe for a trusted decision without a future OS verifier.</summary>
    public bool CanUseForTrustedDecision => false;
    public bool RequiresOperatingSystemIdentityResolution =>
        Status == ExecutablePathPreflightStatus.LexicallyValidButUnverified;
}

/// <summary>
/// Rejects path spellings with known Windows alias ambiguity. A success result means
/// only that the spelling passed syntax checks; reparse points, hard links, WOW64
/// redirection and file replacement still require a future OS-backed identity verifier.
/// </summary>
public static class ExecutablePathPreflight
{
    private static readonly Regex DriveRootedPath = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant);
    private static readonly Regex ShortNameCandidate = new(@"~[1-9][0-9]*(?:\.|$)", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedDeviceName = new(
        @"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9]|LPT[1-9])(?:\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ExecutablePathPreflightResult Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Result(ExecutablePathPreflightStatus.Invalid, "path-empty");

        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            return Result(ExecutablePathPreflightStatus.UnsupportedNamespace, "device-or-extended-namespace");

        // UNC names can resolve differently with credentials, DFS, or server-side aliases.
        // This simulation deliberately accepts only drive-rooted lexical paths.
        if (!DriveRootedPath.IsMatch(normalized))
            return Result(ExecutablePathPreflightStatus.UnsupportedRoot, "drive-rooted-path-required");

        var segments = normalized[3..].Split('\\', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            return Result(ExecutablePathPreflightStatus.Invalid, "empty-or-traversal-segment");

        foreach (var segment in segments)
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return Result(ExecutablePathPreflightStatus.AmbiguousAlias, "trailing-space-or-dot-alias");
            if (segment.Contains(':'))
                return Result(ExecutablePathPreflightStatus.AmbiguousAlias, "alternate-data-stream-or-colon-alias");
            if (ShortNameCandidate.IsMatch(segment))
                return Result(ExecutablePathPreflightStatus.AmbiguousAlias, "short-name-candidate");
            var baseName = segment.Split('.')[0];
            if (ReservedDeviceName.IsMatch(baseName))
                return Result(ExecutablePathPreflightStatus.AmbiguousAlias, "reserved-device-name");
        }

        return Result(ExecutablePathPreflightStatus.LexicallyValidButUnverified, "requires-os-identity-resolution");
    }

    private static ExecutablePathPreflightResult Result(ExecutablePathPreflightStatus status, string reason) => new(status, reason);
}
