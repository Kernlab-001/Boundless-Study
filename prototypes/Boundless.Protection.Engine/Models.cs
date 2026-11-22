using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Boundless.Protection.Engine;

public enum ProtectionProviderKind { AppLocker, Wfp }

public enum ProtectionState
{
    Prepared,
    Applying,
    Active,
    Partial,
    Restoring,
    Restored,
    Conflict,
    Unavailable,
    Failed
}

public enum ProtectionOutcome
{
    Applied,
    Duplicate,
    Conflict,
    Partial,
    ProviderUnavailable,
    PermissionDenied,
    Restored,
    NoOp,
    Failed
}

public enum ProtectionFailureCode
{
    None,
    InvalidRequest,
    DuplicateRequestConflict,
    PolicyDrift,
    OwnershipMismatch,
    RuleIdConflict,
    PermissionDenied,
    ProviderUnavailable,
    PartialApplication,
    ApplicationCrash,
    RestoreFailure,
    JournalConflict
}

public sealed record ApplicationIdentity(string ExecutablePath, string Sha256)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ExecutablePath)
        && (Regex.IsMatch(ExecutablePath, @"^[A-Za-z]:\\") || ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal))
        && Regex.IsMatch(Sha256, "^[A-Fa-f0-9]{64}$");

    internal string CanonicalPath => ExecutablePath.Trim().Replace('/', '\\').ToUpperInvariant();
}

public sealed record RequestedRule(Guid RuleId, string SubjectSid, ApplicationIdentity Identity);

/// <summary>OwnerId is Guid.Empty only for simulated external/administrator rules.</summary>
/// <summary>OwnerId is an opaque authority identifier; provider code never infers ownership from rule content.</summary>
public sealed record ProtectionRule(Guid RuleId, Guid OwnerId, string SubjectSid, ApplicationIdentity Identity);

public sealed record ProtectionRequest(
    Guid RequestId,
    Guid SessionId,
    long SessionGeneration,
    string SubjectSid,
    ProtectionProviderKind ProviderKind,
    IReadOnlyList<RequestedRule> Rules);

public sealed record PolicySnapshot
{
    public PolicySnapshot(ProtectionProviderKind providerKind, long version, IEnumerable<ProtectionRule> rules)
    {
        ProviderKind = providerKind;
        Version = version;
        var ordered = rules.OrderBy(r => r.RuleId).ToArray();
        Rules = new ReadOnlyCollection<ProtectionRule>(ordered);
        Fingerprint = ComputeFingerprint(ordered);
    }

    public ProtectionProviderKind ProviderKind { get; }
    public long Version { get; }
    public IReadOnlyList<ProtectionRule> Rules { get; }
    public string Fingerprint { get; }

    internal static string ComputeFingerprint(IEnumerable<ProtectionRule> rules)
    {
        var canonical = rules.OrderBy(r => r.RuleId).Select(r => new
        {
            r.RuleId,
            r.OwnerId,
            r.SubjectSid,
            Path = r.Identity.CanonicalPath,
            Sha256 = r.Identity.Sha256.ToUpperInvariant()
        });
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}

public sealed record ProtectionOperationRecord(
    Guid RequestId,
    Guid SessionId,
    long SessionGeneration,
    Guid OwnerId,
    string SubjectSid,
    ProtectionProviderKind ProviderKind,
    string PayloadFingerprint,
    PolicySnapshot Baseline,
    IReadOnlyList<RequestedRule> RequestedRules,
    IReadOnlyList<ProtectionRule> AppliedRules,
    ProtectionState State,
    ProtectionFailureCode LastFailure);

public sealed record ProtectionResult(
    ProtectionOutcome Outcome,
    ProtectionState State,
    ProtectionFailureCode FailureCode,
    int AppliedRuleCount,
    int RemovedRuleCount,
    Guid? OwnerId = null,
    string? PolicyFingerprint = null);

public sealed record ProviderMutationResult(
    ProtectionOutcome Outcome,
    ProtectionFailureCode FailureCode,
    IReadOnlyList<ProtectionRule> AffectedRules);

public sealed record ProtectionAuditEvent(
    Guid RequestId,
    Guid SessionId,
    ProtectionProviderKind ProviderKind,
    string Operation,
    ProtectionOutcome Outcome,
    ProtectionFailureCode FailureCode,
    int AffectedRuleCount);

public sealed class SimulatedApplicationCrashException : Exception
{
    public SimulatedApplicationCrashException() : base("Simulated process interruption during policy mutation.") { }
}

public static class ProtectionStateMachine
{
    private static readonly IReadOnlyDictionary<ProtectionState, HashSet<ProtectionState>> Allowed =
        new Dictionary<ProtectionState, HashSet<ProtectionState>>
        {
            [ProtectionState.Prepared] = [ProtectionState.Applying, ProtectionState.Restoring, ProtectionState.Failed, ProtectionState.Unavailable],
            [ProtectionState.Applying] = [ProtectionState.Active, ProtectionState.Partial, ProtectionState.Restoring, ProtectionState.Conflict, ProtectionState.Unavailable, ProtectionState.Failed],
            [ProtectionState.Active] = [ProtectionState.Restoring, ProtectionState.Unavailable, ProtectionState.Conflict],
            [ProtectionState.Partial] = [ProtectionState.Restoring, ProtectionState.Unavailable, ProtectionState.Conflict],
            [ProtectionState.Restoring] = [ProtectionState.Restored, ProtectionState.Partial, ProtectionState.Conflict, ProtectionState.Unavailable, ProtectionState.Failed],
            [ProtectionState.Unavailable] = [ProtectionState.Restoring, ProtectionState.Applying, ProtectionState.Failed, ProtectionState.Conflict],
            [ProtectionState.Failed] = [ProtectionState.Restoring, ProtectionState.Conflict],
            [ProtectionState.Conflict] = [ProtectionState.Restoring],
            [ProtectionState.Restored] = []
        };

    public static bool CanTransition(ProtectionState from, ProtectionState to) => Allowed[from].Contains(to);

    public static ProtectionOperationRecord Transition(ProtectionOperationRecord record, ProtectionState next, ProtectionFailureCode failure = ProtectionFailureCode.None)
    {
        if (record.State == next) return record with { LastFailure = failure };
        if (!CanTransition(record.State, next))
            throw new InvalidOperationException($"Invalid protection transition: {record.State} -> {next}.");
        return record with { State = next, LastFailure = failure };
    }
}

internal static class ProtectionRequestValidation
{
    private static readonly Regex SidPattern = new(@"^S-1-(?:\d+-){1,14}\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProtectionFailureCode Validate(ProtectionRequest request)
    {
        if (request.RequestId == Guid.Empty || request.SessionId == Guid.Empty || request.SessionGeneration <= 0
            || !SidPattern.IsMatch(request.SubjectSid ?? string.Empty) || request.Rules is null || request.Rules.Count == 0)
            return ProtectionFailureCode.InvalidRequest;

        if (request.Rules.Any(rule => rule.RuleId == Guid.Empty
            || !string.Equals(rule.SubjectSid, request.SubjectSid, StringComparison.Ordinal)
            || rule.Identity is null || !rule.Identity.IsValid))
            return ProtectionFailureCode.InvalidRequest;

        if (request.Rules.Select(r => r.RuleId).Distinct().Count() != request.Rules.Count)
            return ProtectionFailureCode.InvalidRequest;

        return ProtectionFailureCode.None;
    }

    public static string ComputeFingerprint(ProtectionRequest request)
    {
        var canonical = new
        {
            request.SessionId,
            request.SessionGeneration,
            request.SubjectSid,
            request.ProviderKind,
            Rules = request.Rules.OrderBy(r => r.RuleId).Select(r => new
            {
                r.RuleId,
                r.SubjectSid,
                Path = r.Identity.CanonicalPath,
                Sha256 = r.Identity.Sha256.ToUpperInvariant()
            })
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}
