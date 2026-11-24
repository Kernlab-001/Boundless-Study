using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Boundless.Protection.Engine;

/// <summary>Target categories evaluated as data only; this file performs no process or OS inspection.</summary>
public enum PolicyTargetKind { Application, NetworkDestination }
public enum PolicyRuleEffect { Allow, Deny, Exception }
public enum PolicyDecision { Allow, Restrict, ExcludedProtectedTarget }
public enum PolicyEvaluationStatus
{
    AllowedByRule,
    RestrictedByRule,
    AllowedByException,
    ExcludedProtectedTarget,
    InvalidRequest,
    InactiveSession,
    IdentityMismatch,
    StaleProcessIdentity,
    IdentityUnavailable,
    IdentityUnsupported,
    IdentityRejected,
    RestrictedByDefault
}
public enum NetworkProtocol { Any, Tcp, Udp }

/// <summary>A canonical application or network target. Only values supplied by the caller are examined.</summary>
public sealed record PolicyTarget(
    PolicyTargetKind Kind,
    string? ExecutablePath = null,
    string? Sha256 = null,
    string? Host = null,
    int? Port = null,
    NetworkProtocol Protocol = NetworkProtocol.Any)
{
    public static PolicyTarget Application(string executablePath, string sha256) =>
        new(PolicyTargetKind.Application, ExecutablePath: executablePath, Sha256: sha256);

    public static PolicyTarget Network(string host, int? port = null, NetworkProtocol protocol = NetworkProtocol.Any) =>
        new(PolicyTargetKind.NetworkDestination, Host: host, Port: port, Protocol: protocol);

    public bool IsValid => Kind switch
    {
        PolicyTargetKind.Application => IsWindowsPath(ExecutablePath)
            && ExecutablePathPreflight.Inspect(ExecutablePath).Status == ExecutablePathPreflightStatus.LexicallyValidButUnverified
            && IsSha256(Sha256)
            && Host is null && Port is null && Protocol == NetworkProtocol.Any,
        PolicyTargetKind.NetworkDestination => IsValidHost(Host) && (Port is null or >= 1 and <= 65535)
            && ExecutablePath is null && Sha256 is null && Enum.IsDefined(Protocol),
        _ => false
    };

    public string CanonicalKey => Kind switch
    {
        PolicyTargetKind.Application => $"app:{CanonicalWindowsPath(ExecutablePath!)}:{Sha256!.ToUpperInvariant()}",
        PolicyTargetKind.NetworkDestination => $"net:{CanonicalHost(Host!)}:{Port?.ToString(CultureInfo.InvariantCulture) ?? "*"}:{Protocol}",
        _ => string.Empty
    };

    internal static string CanonicalWindowsPath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        string root;
        string remainder;
        if (Regex.IsMatch(normalized, @"^[A-Za-z]:\\"))
        {
            root = normalized[..3].ToUpperInvariant();
            remainder = normalized[3..];
        }
        else
        {
            var pieces = normalized.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length < 2) return normalized.ToUpperInvariant();
            root = $"\\\\{pieces[0]}\\{pieces[1]}\\".ToUpperInvariant();
            remainder = string.Join('\\', pieces.Skip(2));
        }

        var segments = new List<string>();
        foreach (var segment in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return (root + string.Join('\\', segments)).ToUpperInvariant();
    }

    private static bool IsWindowsPath(string? path) => !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
        && (Regex.IsMatch(path, @"^[A-Za-z]:\\") || path.StartsWith(@"\\", StringComparison.Ordinal));

    private static bool IsSha256(string? hash) => hash is not null && Regex.IsMatch(hash, "^[A-Fa-f0-9]{64}$");

    private static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains('\\') || host.Contains('@')) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        try
        {
            var ascii = new IdnMapping().GetAscii(host.Trim().TrimEnd('.'));
            return ascii.Length is > 0 and <= 253
                && ascii.Split('.').All(label => label.Length is > 0 and <= 63
                    && Regex.IsMatch(label, @"^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$"));
        }
        catch (ArgumentException) { return false; }
    }

    private static string CanonicalHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        return IPAddress.TryParse(trimmed, out var address)
            ? address.ToString().ToUpperInvariant()
            : new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
    }
}

/// <summary>Immutable, caller-captured process identity evidence; no PID lookup occurs in the evaluator.</summary>
public sealed record ProcessIdentitySnapshot(
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    DateTimeOffset CapturedAtUtc,
    string SubjectSid,
    string ExecutablePath,
    string ExecutableSha256);

public sealed record ProtectionSessionContext(
    Guid SessionId,
    long Generation,
    string SubjectSid,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsActive);

public sealed record EvaluationRule(
    Guid RuleId,
    string SubjectSid,
    PolicyTarget Target,
    PolicyRuleEffect Effect,
    int Priority = 0,
    bool IsEnabled = true,
    Guid SessionId = default,
    long SessionGeneration = 0);

public sealed record PolicyEvaluationRequest(
    ProtectionSessionContext Session,
    ProcessIdentitySnapshot Process,
    PolicyTarget Target,
    DateTimeOffset EvaluatedAtUtc);

public sealed record PolicyEvaluationResult(
    PolicyDecision Decision,
    PolicyEvaluationStatus Status,
    bool FailClosed,
    Guid? MatchedRuleId = null,
    string ReasonCode = "")
{
    /// <summary>The result describes a simulated decision only, never active OS enforcement.</summary>
    public bool IsSimulation => true;
    public bool CanClaimProtection => false;
}

/// <summary>
/// Catalog supplied by the host product's policy author. No filesystem or platform
/// discovery is performed. Paths use lexical dot-segment normalization only; filesystem
/// aliases such as junctions, symlinks, and 8.3 names are not resolved here.
/// </summary>
public sealed class ProtectedTargetCatalog
{
    private static readonly string[] AccessibilityExecutables =
    [
        "Narrator.exe", "osk.exe", "magnify.exe", "utilman.exe", "sethc.exe"
    ];

    private static readonly string[] CoreSystemExecutables =
    [
        "csrss.exe", "lsass.exe", "services.exe", "smss.exe", "svchost.exe",
        "wininit.exe", "winlogon.exe", "dwm.exe", "explorer.exe", "taskmgr.exe"
    ];

    private readonly HashSet<string> _applicationPaths;
    private readonly HashSet<string> _networkKeys;

    public ProtectedTargetCatalog(IEnumerable<string>? additionalApplicationPaths = null, IEnumerable<PolicyTarget>? additionalNetworkTargets = null)
    {
        _applicationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _networkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalApplicationPaths is not null)
            foreach (var path in additionalApplicationPaths)
            {
                if (ExecutablePathPreflight.Inspect(path).Status != ExecutablePathPreflightStatus.LexicallyValidButUnverified)
                    throw new ArgumentException("Protected application paths must use an unambiguous drive-rooted lexical form.", nameof(additionalApplicationPaths));
                _applicationPaths.Add(PolicyTarget.CanonicalWindowsPath(path));
            }
        if (additionalNetworkTargets is not null)
            foreach (var target in additionalNetworkTargets)
            {
                if (target is null || !target.IsValid || target.Kind != PolicyTargetKind.NetworkDestination)
                    throw new ArgumentException("Protected network targets must be valid network destinations.", nameof(additionalNetworkTargets));
                _networkKeys.Add(target.CanonicalKey);
            }
    }

    public static ProtectedTargetCatalog ForWindowsDirectory(string windowsDirectory, IEnumerable<string>? additionalApplicationPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        if (ExecutablePathPreflight.Inspect(windowsDirectory).Status != ExecutablePathPreflightStatus.LexicallyValidButUnverified)
            throw new ArgumentException("Windows directory must use an unambiguous drive-rooted lexical form.", nameof(windowsDirectory));
        var root = PolicyTarget.CanonicalWindowsPath(windowsDirectory).TrimEnd('\\');

        // Include common lexical aliases used by 32-bit callers. Reparse points,
        // 8.3 names, and arbitrary junction aliases remain unresolved without OS IO.
        var paths = AccessibilityExecutables.Concat(CoreSystemExecutables)
            .SelectMany(name => new[]
            {
                $"{root}\\System32\\{name}",
                $"{root}\\SysWOW64\\{name}",
                $"{root}\\Sysnative\\{name}"
            })
            .Concat(additionalApplicationPaths ?? []);
        return new ProtectedTargetCatalog(paths);
    }

    public bool IsProtected(PolicyTarget target) => target.Kind switch
    {
        PolicyTargetKind.Application => _applicationPaths.Contains(PolicyTarget.CanonicalWindowsPath(target.ExecutablePath!)),
        PolicyTargetKind.NetworkDestination => _networkKeys.Contains(target.CanonicalKey),
        _ => false
    };
}

/// <summary>
/// Pure deterministic policy evaluation. Session and process evidence is caller-supplied
/// and untrusted; this type does not authenticate a SID, PID, image, or live OS process.
/// It computes a decision only and never claims operating-system enforcement.
/// </summary>
public sealed class ProtectionPolicyEvaluator
{
    private static readonly Regex SidPattern = new(@"^S-1-(?:\d+-){1,14}\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan LiveVerificationClockEntryTolerance = TimeSpan.FromMilliseconds(250);
    private readonly ProtectedTargetCatalog _protectedTargets;
    private readonly TimeSpan _maximumIdentityAge;
    private readonly IIdentityBoundaryVerifier? _identityVerifier;
    private readonly Func<bool> _isWindows;

    public ProtectionPolicyEvaluator(ProtectedTargetCatalog protectedTargets, TimeSpan? maximumIdentityAge = null)
        : this(protectedTargets, maximumIdentityAge, identityVerifier: null, isWindows: null)
    {
    }

    internal ProtectionPolicyEvaluator(
        ProtectedTargetCatalog protectedTargets,
        TimeSpan? maximumIdentityAge,
        IIdentityBoundaryVerifier? identityVerifier,
        Func<bool>? isWindows = null)
    {
        _protectedTargets = protectedTargets ?? throw new ArgumentNullException(nameof(protectedTargets));
        _maximumIdentityAge = maximumIdentityAge ?? TimeSpan.FromSeconds(30);
        if (_maximumIdentityAge <= TimeSpan.Zero || _maximumIdentityAge > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumIdentityAge), "Identity freshness must be between zero and five minutes.");
        _identityVerifier = identityVerifier;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    /// <summary>
    /// Authorization entry point backed by OS identity verification on Windows.
    /// Caller-supplied claims are verified via the configured <see cref="IIdentityBoundaryVerifier"/>
    /// (defaulting to <see cref="WindowsIdentityBoundaryVerifier.Default"/> on Windows) and never
    /// promoted without OS verification. Non-Windows platforms fail closed with
    /// <see cref="PolicyEvaluationStatus.IdentityUnsupported"/>.
    /// Use <see cref="Evaluate"/> only for simulation.
    /// </summary>
    public Task<PolicyEvaluationResult> EvaluateAuthorizedAsync(
        CallerSessionIdentityClaims sessionClaims,
        CallerProcessIdentityClaims processClaims,
        PolicyTarget target,
        DateTimeOffset evaluatedAtUtc,
        IEnumerable<EvaluationRule>? rules,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionClaims);
        ArgumentNullException.ThrowIfNull(processClaims);

        if (!_isWindows())
        {
            return Task.FromResult(
                Closed(PolicyEvaluationStatus.IdentityUnsupported, "platform-unsupported-windows-identity-required"));
        }

        var verifier = _identityVerifier ?? WindowsIdentityBoundaryVerifier.Default;
        return EvaluateAuthorizedAsync(
            verifier,
            sessionClaims,
            processClaims,
            target,
            evaluatedAtUtc,
            rules,
            cancellationToken);
    }

    /// <summary>
    /// Trusted-verifier path reserved to this assembly. The verifier interface and
    /// evidence constructors are internal so external callers cannot claim the OS
    /// verified source flag or mint evidence.
    /// </summary>
    internal async Task<PolicyEvaluationResult> EvaluateAuthorizedAsync(
        IIdentityBoundaryVerifier? verifier,
        CallerSessionIdentityClaims sessionClaims,
        CallerProcessIdentityClaims processClaims,
        PolicyTarget target,
        DateTimeOffset evaluatedAtUtc,
        IEnumerable<EvaluationRule>? rules,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sessionClaims);
        ArgumentNullException.ThrowIfNull(processClaims);

        if (verifier is null) return IdentityUnavailable("trusted-identity-verifier-unavailable");
        if (verifier.SourceTrust != IdentityEvidenceSourceTrust.OperatingSystemVerified)
            return Closed(PolicyEvaluationStatus.IdentityUnsupported, "verifier-source-not-trusted");

        var verificationStartedUtc = DateTimeOffset.UtcNow;
        var sessionResult = await verifier.VerifySessionAsync(sessionClaims, cancellationToken).ConfigureAwait(false);
        if (sessionResult.Status != IdentityVerificationStatus.Verified)
            return FromVerificationFailure(sessionResult.Status, sessionResult.ReasonCode);

        var session = sessionResult.Evidence!;
        var sessionEvaluatedAtUtc = ResolveLiveVerificationTimestamp(
            verifier, evaluatedAtUtc, verificationStartedUtc, session.AuthenticatedAtUtc, session.ExpiresAtUtc);
        if (!IdentityTrustBoundary.IsTrustedSession(verifier, sessionResult)
            || !SessionEvidenceMatchesClaims(session, sessionClaims, sessionEvaluatedAtUtc))
            return Closed(PolicyEvaluationStatus.IdentityRejected, "verified-session-does-not-match-claims");

        var processResult = await verifier.VerifyProcessAsync(processClaims, session, cancellationToken).ConfigureAwait(false);
        if (processResult.Status != IdentityVerificationStatus.Verified)
            return FromVerificationFailure(processResult.Status, processResult.ReasonCode);

        var process = processResult.Evidence!;
        var effectiveEvaluatedAtUtc = ResolveLiveVerificationTimestamp(
            verifier, sessionEvaluatedAtUtc, verificationStartedUtc, process.ObservedAtUtc, session.ExpiresAtUtc);
        if (!IdentityTrustBoundary.IsTrustedProcess(verifier, processResult, session)
            || !ProcessEvidenceMatchesClaims(process, processClaims, effectiveEvaluatedAtUtc))
            return Closed(PolicyEvaluationStatus.IdentityRejected, "verified-process-does-not-match-claims");

        var request = new PolicyEvaluationRequest(
            new ProtectionSessionContext(session.SessionId, session.Generation, session.SubjectSid,
                session.StartsAtUtc, session.ExpiresAtUtc, true),
            new ProcessIdentitySnapshot(process.ProcessId, process.ProcessStartUtc, process.ObservedAtUtc,
                process.SubjectSid, process.ExecutablePath, process.ExecutableSha256),
            target,
            effectiveEvaluatedAtUtc);
        return Evaluate(request, rules);
    }

    private static DateTimeOffset ResolveLiveVerificationTimestamp(
        IIdentityBoundaryVerifier verifier,
        DateTimeOffset evaluatedAtUtc,
        DateTimeOffset verificationStartedUtc,
        DateTimeOffset evidenceObservedAtUtc,
        DateTimeOffset sessionExpiresAtUtc)
    {
        if (verifier is not WindowsIdentityBoundaryVerifier)
            return evaluatedAtUtc;

        var verificationNowUtc = DateTimeOffset.UtcNow;
        if (evaluatedAtUtc.Offset != TimeSpan.Zero
            || evaluatedAtUtc < verificationStartedUtc - LiveVerificationClockEntryTolerance
            || evaluatedAtUtc > verificationNowUtc)
        {
            return evaluatedAtUtc;
        }

        if (evidenceObservedAtUtc >= verificationStartedUtc
            && evidenceObservedAtUtc <= verificationNowUtc
            && evidenceObservedAtUtc > evaluatedAtUtc
            && evidenceObservedAtUtc < sessionExpiresAtUtc)
        {
            return evidenceObservedAtUtc;
        }

        return evaluatedAtUtc;
    }

    private bool SessionEvidenceMatchesClaims(
        VerifiedSessionIdentity evidence,
        CallerSessionIdentityClaims claims,
        DateTimeOffset evaluatedAtUtc) =>
        evidence.SessionId != Guid.Empty
        && evidence.Generation > 0
        && string.Equals(evidence.SubjectSid, claims.SubjectSid, StringComparison.Ordinal)
        && evidence.SessionId == claims.SessionId
        && evidence.Generation == claims.SessionGeneration
        && (claims.AuthenticationContext is null
            || string.Equals(evidence.AuthenticationContext, claims.AuthenticationContext, StringComparison.Ordinal))
        && !string.IsNullOrWhiteSpace(evidence.AuthenticationContext)
        && evidence.AuthenticatedAtUtc.Offset == TimeSpan.Zero
        && evidence.StartsAtUtc.Offset == TimeSpan.Zero
        && evidence.ExpiresAtUtc.Offset == TimeSpan.Zero
        && evidence.StartsAtUtc < evidence.ExpiresAtUtc
        && evidence.AuthenticatedAtUtc <= evaluatedAtUtc
        && evaluatedAtUtc >= evidence.StartsAtUtc
        && evaluatedAtUtc < evidence.ExpiresAtUtc;

    private bool ProcessEvidenceMatchesClaims(
        VerifiedProcessIdentity evidence,
        CallerProcessIdentityClaims claims,
        DateTimeOffset evaluatedAtUtc)
    {
        if (evidence.ProcessId <= 0 || evidence.ProcessId != claims.ProcessId
            || evidence.ProcessStartUtc != claims.ProcessStartUtc
            || !string.Equals(evidence.SubjectSid, claims.SubjectSid, StringComparison.Ordinal)
            || evidence.ProcessStartUtc.Offset != TimeSpan.Zero || evidence.ObservedAtUtc.Offset != TimeSpan.Zero
            || evidence.ProcessStartUtc > evidence.ObservedAtUtc || evidence.ObservedAtUtc > evaluatedAtUtc)
            return false;
        if (evaluatedAtUtc - evidence.ObservedAtUtc > _maximumIdentityAge) return false;
        var verifiedTarget = PolicyTarget.Application(evidence.ExecutablePath, evidence.ExecutableSha256);
        var claimedTarget = PolicyTarget.Application(claims.ExecutablePath, claims.ExecutableSha256);
        return verifiedTarget.IsValid && claimedTarget.IsValid
            && string.Equals(verifiedTarget.CanonicalKey, claimedTarget.CanonicalKey, StringComparison.Ordinal);
    }

    private static PolicyEvaluationResult FromVerificationFailure(IdentityVerificationStatus status, string reasonCode) => status switch
    {
        IdentityVerificationStatus.Unavailable => IdentityUnavailable(reasonCode),
        IdentityVerificationStatus.Unsupported => Closed(PolicyEvaluationStatus.IdentityUnsupported, reasonCode),
        _ => Closed(PolicyEvaluationStatus.IdentityRejected, reasonCode)
    };

    private static PolicyEvaluationResult IdentityUnavailable(string reason) =>
        Closed(PolicyEvaluationStatus.IdentityUnavailable, reason);

    public PolicyEvaluationResult Evaluate(PolicyEvaluationRequest request, IEnumerable<EvaluationRule>? rules)
    {
        if (rules is null || request is null || request.Session is null || request.Process is null || request.Target is null
            || !request.Target.IsValid || !Enum.IsDefined(request.Target.Kind)
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
            return Closed(PolicyEvaluationStatus.InvalidRequest, "invalid-request");

        var session = request.Session;
        if (session.SessionId == Guid.Empty || session.Generation <= 0 || !ValidSid(session.SubjectSid)
            || session.StartsAtUtc.Offset != TimeSpan.Zero || session.ExpiresAtUtc.Offset != TimeSpan.Zero
            || session.StartsAtUtc >= session.ExpiresAtUtc || !session.IsActive
            || request.EvaluatedAtUtc < session.StartsAtUtc || request.EvaluatedAtUtc >= session.ExpiresAtUtc)
            return Closed(PolicyEvaluationStatus.InactiveSession, "session-inactive-or-invalid");

        var process = request.Process;
        if (process.ProcessId <= 0 || !ValidSid(process.SubjectSid) || process.ProcessStartUtc.Offset != TimeSpan.Zero
            || process.CapturedAtUtc.Offset != TimeSpan.Zero || process.ProcessStartUtc > process.CapturedAtUtc
            || !PolicyTarget.Application(process.ExecutablePath, process.ExecutableSha256).IsValid)
            return Closed(PolicyEvaluationStatus.InvalidRequest, "invalid-process-identity");
        if (!string.Equals(process.SubjectSid, session.SubjectSid, StringComparison.Ordinal))
            return Closed(PolicyEvaluationStatus.IdentityMismatch, "user-scope-mismatch");
        var identityAge = request.EvaluatedAtUtc - process.CapturedAtUtc;
        if (identityAge < TimeSpan.Zero || identityAge > _maximumIdentityAge || process.ProcessStartUtc > request.EvaluatedAtUtc)
            return Closed(PolicyEvaluationStatus.StaleProcessIdentity, "process-identity-stale");

        var processTarget = PolicyTarget.Application(process.ExecutablePath, process.ExecutableSha256);
        if (request.Target.Kind == PolicyTargetKind.Application
            && !string.Equals(processTarget.CanonicalKey, request.Target.CanonicalKey, StringComparison.Ordinal))
            return Closed(PolicyEvaluationStatus.IdentityMismatch, "target-process-identity-mismatch");
        if (_protectedTargets.IsProtected(processTarget) || _protectedTargets.IsProtected(request.Target))
            return new PolicyEvaluationResult(PolicyDecision.ExcludedProtectedTarget,
                PolicyEvaluationStatus.ExcludedProtectedTarget, false, ReasonCode: "protected-target-exclusion");

        EvaluationRule[] enabledRules;
        try
        {
            var allRules = rules.ToArray();
            if (allRules.Any(rule => rule is null))
                return Closed(PolicyEvaluationStatus.InvalidRequest, "null-policy-rule");
            enabledRules = allRules.Where(rule => rule is not null && rule.IsEnabled).ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Closed(PolicyEvaluationStatus.InvalidRequest, "invalid-policy-data");
        }

        if (enabledRules.Any(rule => rule.RuleId == Guid.Empty || !ValidSid(rule.SubjectSid)
            || rule.Target is null || !rule.Target.IsValid || !Enum.IsDefined(rule.Effect)
            || rule.SessionId == Guid.Empty || rule.SessionGeneration <= 0))
            return Closed(PolicyEvaluationStatus.InvalidRequest, "malformed-enabled-policy-rule");

        var applicable = enabledRules.Where(rule => rule.SessionId == session.SessionId
                && rule.SessionGeneration == session.Generation
                && string.Equals(rule.SubjectSid, session.SubjectSid, StringComparison.Ordinal)
                && string.Equals(rule.Target.CanonicalKey, request.Target.CanonicalKey, StringComparison.Ordinal))
            .ToArray();

        // Precedence is explicit and stable: exception > deny > allow > default restrict.
        // Priority breaks ties within an effect; RuleId provides deterministic final ordering.
        var matched = applicable
            .OrderBy(rule => EffectRank(rule.Effect))
            .ThenByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.RuleId)
            .FirstOrDefault();

        return matched?.Effect switch
        {
            PolicyRuleEffect.Exception => new PolicyEvaluationResult(PolicyDecision.Allow, PolicyEvaluationStatus.AllowedByException, false, matched.RuleId, "exception-overrides-restriction"),
            PolicyRuleEffect.Allow => new PolicyEvaluationResult(PolicyDecision.Allow, PolicyEvaluationStatus.AllowedByRule, false, matched.RuleId, "explicit-allow"),
            PolicyRuleEffect.Deny => new PolicyEvaluationResult(PolicyDecision.Restrict, PolicyEvaluationStatus.RestrictedByRule, true, matched.RuleId, "explicit-deny"),
            _ => new PolicyEvaluationResult(PolicyDecision.Restrict, PolicyEvaluationStatus.RestrictedByDefault, true, ReasonCode: "no-matching-rule-fail-closed")
        };
    }

    private static int EffectRank(PolicyRuleEffect effect) => effect switch
    {
        PolicyRuleEffect.Exception => 0,
        PolicyRuleEffect.Deny => 1,
        PolicyRuleEffect.Allow => 2,
        _ => int.MaxValue
    };

    private static bool ValidSid(string? sid) => sid is not null && SidPattern.IsMatch(sid);

    private static PolicyEvaluationResult Closed(PolicyEvaluationStatus status, string reason) =>
        new(PolicyDecision.Restrict, status, true, ReasonCode: reason);
}
