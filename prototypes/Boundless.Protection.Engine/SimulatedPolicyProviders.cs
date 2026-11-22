namespace Boundless.Protection.Engine;

/// <summary>
/// In-memory simulation only. This type has no Windows API, registry, service, firewall,
/// process, or native interop dependency.
/// </summary>
public abstract class SimulatedPolicyProvider : IProtectionProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProtectionRule> _rules = [];
    private long _version;
    private bool _available = true;
    private bool _denyWrites;
    private int? _failApplyAfter;
    private int? _crashApplyAfter;
    private bool _failRestore;

    protected SimulatedPolicyProvider(ProtectionProviderKind kind) => Kind = kind;

    public ProtectionProviderKind Kind { get; }
    public ProtectionProviderKind ProviderKind => Kind;

    public void SetAvailable(bool available) { lock (_gate) _available = available; }
    public void DenyWrites(bool deny) { lock (_gate) _denyWrites = deny; }
    public void FailApplyAfter(int appliedCount) { lock (_gate) _failApplyAfter = Math.Max(0, appliedCount); }
    public void CrashApplyAfter(int appliedCount) { lock (_gate) _crashApplyAfter = Math.Max(0, appliedCount); }
    public void FailRestore(bool fail) { lock (_gate) _failRestore = fail; }

    public void AddExternalRule(ProtectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            if (!_rules.TryAdd(rule.RuleId, rule)) throw new InvalidOperationException("Rule ID already exists.");
            _version++;
        }
    }

    public void ModifyRuleExternally(Guid ruleId)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(ruleId, out var current)) throw new KeyNotFoundException("Rule was not found.");
            var alteredIdentity = current.Identity with { Sha256 = new string('F', 64) };
            _rules[ruleId] = current with { OwnerId = Guid.Empty, Identity = alteredIdentity };
            _version++;
        }
    }

    public void RemoveRuleExternally(Guid ruleId)
    {
        lock (_gate)
        {
            if (!_rules.Remove(ruleId)) throw new KeyNotFoundException("Rule was not found.");
            _version++;
        }
    }

    public Task<PolicySnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureAvailable();
            return Task.FromResult(new PolicySnapshot(Kind, _version, _rules.Values));
        }
    }

    public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureAvailable();
            IReadOnlyList<ProtectionRule> found = _rules.Values.Where(r => r.OwnerId == ownerId).OrderBy(r => r.RuleId).ToArray();
            return Task.FromResult(found);
        }
    }

    public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_available) return Task.FromResult(Result(ProtectionOutcome.ProviderUnavailable, ProtectionFailureCode.ProviderUnavailable));
            if (_denyWrites) return Task.FromResult(Result(ProtectionOutcome.PermissionDenied, ProtectionFailureCode.PermissionDenied));

            var current = new PolicySnapshot(Kind, _version, _rules.Values);
            if (current.Version != expectedVersion || !string.Equals(current.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
                return Task.FromResult(Result(ProtectionOutcome.Conflict, ProtectionFailureCode.PolicyDrift));

            if (rules.Any(r => _rules.ContainsKey(r.RuleId)))
                return Task.FromResult(Result(ProtectionOutcome.Conflict, ProtectionFailureCode.RuleIdConflict));

            var expectedCount = rules.Count;
            var crashAfter = _crashApplyAfter;
            var failAfter = _failApplyAfter;
            _crashApplyAfter = null;
            _failApplyAfter = null;
            var applyCount = Math.Min(expectedCount, crashAfter ?? failAfter ?? expectedCount);
            var added = new List<ProtectionRule>(applyCount);
            foreach (var requested in rules.Take(applyCount))
            {
                var stored = new ProtectionRule(requested.RuleId, ownerId, requested.SubjectSid, requested.Identity);
                _rules.Add(stored.RuleId, stored);
                added.Add(stored);
                _version++;
            }

            if (crashAfter.HasValue) throw new SimulatedApplicationCrashException();
            if (failAfter.HasValue && applyCount < expectedCount)
            {
                var outcome = added.Count == 0 ? ProtectionOutcome.Failed : ProtectionOutcome.Partial;
                return Task.FromResult(new ProviderMutationResult(outcome, ProtectionFailureCode.PartialApplication, added));
            }

            return Task.FromResult(new ProviderMutationResult(ProtectionOutcome.Applied, ProtectionFailureCode.None, added));
        }
    }

    public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactRules, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_available) return Task.FromResult(Result(ProtectionOutcome.ProviderUnavailable, ProtectionFailureCode.ProviderUnavailable));
            if (_denyWrites) return Task.FromResult(Result(ProtectionOutcome.PermissionDenied, ProtectionFailureCode.PermissionDenied));
            if (_failRestore) return Task.FromResult(Result(ProtectionOutcome.Failed, ProtectionFailureCode.RestoreFailure));

            // Plan and validate the entire restore before mutating anything. This
            // models an atomic provider transaction under the simulator lock.
            var conflict = exactRules.Any(expected =>
                _rules.TryGetValue(expected.RuleId, out var current)
                && (current.OwnerId != ownerId || current != expected));
            if (conflict)
                return Task.FromResult(new ProviderMutationResult(
                    ProtectionOutcome.Conflict, ProtectionFailureCode.OwnershipMismatch, Array.Empty<ProtectionRule>()));

            var removed = new List<ProtectionRule>();
            foreach (var expected in exactRules)
            {
                if (!_rules.TryGetValue(expected.RuleId, out var current)) continue;
                _rules.Remove(expected.RuleId);
                _version++;
                removed.Add(current);
            }

            var outcome = removed.Count == 0 ? ProtectionOutcome.NoOp : ProtectionOutcome.Restored;
            var failure = ProtectionFailureCode.None;
            return Task.FromResult(new ProviderMutationResult(outcome, failure, removed));
        }
    }

    private ProviderMutationResult Result(ProtectionOutcome outcome, ProtectionFailureCode failure) =>
        new(outcome, failure, Array.Empty<ProtectionRule>());

    private void EnsureAvailable()
    {
        if (!_available) throw new ProtectionProviderUnavailableException();
    }
}

public sealed class SimulatedAppLockerProvider() : SimulatedPolicyProvider(ProtectionProviderKind.AppLocker) { }

public sealed class SimulatedWfpProvider() : SimulatedPolicyProvider(ProtectionProviderKind.Wfp) { }

public sealed class ProtectionProviderUnavailableException() : Exception("Simulated provider is unavailable.") { }
