using System.Runtime.CompilerServices;

namespace Boundless.Protection.Engine;

/// <summary>
/// Simulation-only coordinator. It validates requests, journals intent, applies changes
/// through injected in-memory providers, and cleans up only exact engine-owned rules.
/// </summary>
public sealed class ProtectionEngine
{
    private readonly IReadOnlyDictionary<ProtectionProviderKind, IProtectionProvider> _providers;
    private readonly IProtectionJournal _journal;
    private readonly IProtectionAuditSink _audit;
    private static readonly ConditionalWeakTable<IProtectionJournal, SemaphoreSlim> SharedOperationGates = new();
    private readonly SemaphoreSlim _operationGate;

    public ProtectionEngine(IEnumerable<IProtectionProvider> providers, IProtectionJournal journal, IProtectionAuditSink audit)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _operationGate = SharedOperationGates.GetValue(_journal, _ => new SemaphoreSlim(1, 1));
        var providerArray = providers.ToArray();
        if (providerArray.Any(p => p is null)) throw new ArgumentException("Providers cannot contain null.", nameof(providers));
        if (providerArray.Select(p => p.Kind).Distinct().Count() != providerArray.Length)
            throw new ArgumentException("Only one provider per kind can be registered.", nameof(providers));
        _providers = providerArray.ToDictionary(p => p.Kind);
    }

    public async Task<ProtectionResult> ApplyAsync(ProtectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Freeze caller-owned collections before waiting on the shared journal gate.
        request = request with
        {
            Rules = request.Rules is null ? null! : Array.AsReadOnly(request.Rules.ToArray())
        };
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProtectionResult> RestoreAsync(Guid sessionId, ProtectionProviderKind providerKind, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
            return Result(ProtectionOutcome.Failed, ProtectionState.Failed, ProtectionFailureCode.InvalidRequest);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = _journal.ForSession(sessionId, providerKind);
            if (records.Count == 0 || records.All(r => r.State == ProtectionState.Restored))
                return Result(ProtectionOutcome.NoOp, ProtectionState.Restored, ProtectionFailureCode.None);

            var outcomes = new List<ProtectionResult>(records.Count);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.State == ProtectionState.Restored)
                {
                    outcomes.Add(Result(ProtectionOutcome.NoOp, ProtectionState.Restored, ProtectionFailureCode.None, record.OwnerId));
                    continue;
                }

                outcomes.Add(await RestoreRecordAsync(record, cancellationToken).ConfigureAwait(false));
            }

            return Aggregate(outcomes);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Simulates restart recovery by reusing the in-memory provider and journal with a
    /// newly constructed engine. This is not durable disk/process/Windows recovery.
    /// </summary>
    public async Task<IReadOnlyList<ProtectionResult>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<ProtectionResult>();
            foreach (var record in _journal.Recoverable())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await RestoreRecordAsync(record, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ProtectionResult> ApplyCoreAsync(ProtectionRequest request, CancellationToken cancellationToken)
    {
        var validation = ProtectionRequestValidation.Validate(request);
        if (validation != ProtectionFailureCode.None)
            return AuditResult(request, "apply", ProtectionOutcome.Failed, ProtectionState.Failed, validation);

        var payloadFingerprint = ProtectionRequestValidation.ComputeFingerprint(request);
        var existing = _journal.Find(request.RequestId);
        if (existing is not null)
        {
            if (!string.Equals(existing.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
                return AuditResult(request, "apply", ProtectionOutcome.Conflict, ProtectionState.Conflict, ProtectionFailureCode.DuplicateRequestConflict, existing.AppliedRules.Count, ownerId: existing.OwnerId);
            return AuditResult(request, "apply", ProtectionOutcome.Duplicate, existing.State, existing.LastFailure, existing.AppliedRules.Count, ownerId: existing.OwnerId);
        }

        var latestGeneration = _journal.All().Where(r => r.SessionId == request.SessionId)
            .Select(r => r.SessionGeneration).DefaultIfEmpty(0).Max();
        if (request.SessionGeneration < latestGeneration)
            return AuditResult(request, "apply", ProtectionOutcome.Conflict, ProtectionState.Conflict, ProtectionFailureCode.JournalConflict);

        if (!_providers.TryGetValue(request.ProviderKind, out var provider))
            return AuditResult(request, "apply", ProtectionOutcome.ProviderUnavailable, ProtectionState.Unavailable, ProtectionFailureCode.ProviderUnavailable);

        PolicySnapshot baseline;
        try
        {
            baseline = await provider.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProtectionProviderUnavailableException)
        {
            return AuditResult(request, "apply", ProtectionOutcome.ProviderUnavailable, ProtectionState.Unavailable, ProtectionFailureCode.ProviderUnavailable);
        }

        var ownerId = request.RequestId;
        var intendedRules = request.Rules.Select(r => new ProtectionRule(r.RuleId, ownerId, r.SubjectSid, r.Identity)).ToArray();
        var prepared = new ProtectionOperationRecord(
            request.RequestId,
            request.SessionId,
            request.SessionGeneration,
            ownerId,
            request.SubjectSid,
            request.ProviderKind,
            payloadFingerprint,
            baseline,
            Array.AsReadOnly(request.Rules.ToArray()),
            Array.Empty<ProtectionRule>(),
            ProtectionState.Prepared,
            ProtectionFailureCode.None);

        if (!_journal.TryAddPrepared(prepared))
        {
            var raced = _journal.Find(request.RequestId);
            if (raced is null)
                return AuditResult(request, "apply", ProtectionOutcome.Failed, ProtectionState.Failed, ProtectionFailureCode.JournalConflict);
            var samePayload = string.Equals(raced.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal);
            return AuditResult(request, "apply", samePayload ? ProtectionOutcome.Duplicate : ProtectionOutcome.Conflict,
                raced.State, samePayload ? raced.LastFailure : ProtectionFailureCode.DuplicateRequestConflict,
                raced.AppliedRules.Count, ownerId: raced.OwnerId);
        }

        var applying = ProtectionStateMachine.Transition(prepared, ProtectionState.Applying);
        _journal.Update(applying);

        ProviderMutationResult mutation;
        try
        {
            mutation = await provider.ApplyAsync(ownerId, request.Rules, baseline.Version, baseline.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtectionProviderUnavailableException)
        {
            var unavailable = ProtectionStateMachine.Transition(applying, ProtectionState.Unavailable, ProtectionFailureCode.ProviderUnavailable);
            _journal.Update(unavailable);
            return AuditResult(request, "apply", ProtectionOutcome.ProviderUnavailable, unavailable.State, unavailable.LastFailure, ownerId: ownerId);
        }

        var appliedRules = mutation.AffectedRules.ToArray();
        var nextState = StateForApply(mutation.Outcome);
        var updated = applying with { AppliedRules = Array.AsReadOnly(appliedRules) };
        updated = ProtectionStateMachine.Transition(updated, nextState, mutation.FailureCode);
        _journal.Update(updated);

        return AuditResult(request, "apply", mutation.Outcome, updated.State, mutation.FailureCode,
            appliedRules.Length, ownerId: ownerId, policyFingerprint: baseline.Fingerprint);
    }

    private async Task<ProtectionResult> RestoreRecordAsync(ProtectionOperationRecord record, CancellationToken cancellationToken)
    {
        if (record.State == ProtectionState.Restored)
            return Result(ProtectionOutcome.NoOp, ProtectionState.Restored, ProtectionFailureCode.None, record.OwnerId);

        if (!_providers.TryGetValue(record.ProviderKind, out var provider))
        {
            var unavailableResult = SetRecordState(record, ProtectionState.Unavailable, ProtectionFailureCode.ProviderUnavailable);
            return AuditResult(record, "restore", ProtectionOutcome.ProviderUnavailable, unavailableResult.State, unavailableResult.LastFailure, 0);
        }

        var restoring = record.State == ProtectionState.Restoring
            ? record
            : ProtectionStateMachine.Transition(record, ProtectionState.Restoring);
        _journal.Update(restoring);

        try
        {
            // The old snapshot is deliberately not used as a restore payload. Removal
            // is limited to this owner and exact requested content; current external
            // policy is preserved.
            var exactRules = restoring.RequestedRules
                .Select(r => new ProtectionRule(r.RuleId, restoring.OwnerId, r.SubjectSid, r.Identity))
                .ToArray();
            var mutation = await provider.RestoreAsync(restoring.OwnerId, exactRules, cancellationToken).ConfigureAwait(false);

            if (mutation.Outcome == ProtectionOutcome.Conflict)
            {
                var conflict = ProtectionStateMachine.Transition(restoring, ProtectionState.Conflict, mutation.FailureCode);
                _journal.Update(conflict);
                return AuditResult(restoring, "restore", ProtectionOutcome.Conflict, conflict.State, conflict.LastFailure, mutation.AffectedRules.Count);
            }

            if (mutation.Outcome is ProtectionOutcome.ProviderUnavailable or ProtectionOutcome.PermissionDenied or ProtectionOutcome.Failed)
            {
                var state = mutation.Outcome == ProtectionOutcome.ProviderUnavailable ? ProtectionState.Unavailable : ProtectionState.Failed;
                var failed = ProtectionStateMachine.Transition(restoring, state, mutation.FailureCode);
                _journal.Update(failed);
                return AuditResult(restoring, "restore", mutation.Outcome, failed.State, failed.LastFailure, mutation.AffectedRules.Count);
            }

            var restored = ProtectionStateMachine.Transition(restoring, ProtectionState.Restored);
            _journal.Update(restored);
            var outcome = mutation.Outcome == ProtectionOutcome.NoOp ? ProtectionOutcome.NoOp : ProtectionOutcome.Restored;
            return AuditResult(restoring, "restore", outcome, restored.State, ProtectionFailureCode.None, mutation.AffectedRules.Count);
        }
        catch (ProtectionProviderUnavailableException)
        {
            var unavailable = ProtectionStateMachine.Transition(restoring, ProtectionState.Unavailable, ProtectionFailureCode.ProviderUnavailable);
            _journal.Update(unavailable);
            return AuditResult(restoring, "restore", ProtectionOutcome.ProviderUnavailable, unavailable.State, unavailable.LastFailure, 0);
        }
    }

    private static ProtectionState StateForApply(ProtectionOutcome outcome) => outcome switch
    {
        ProtectionOutcome.Applied => ProtectionState.Active,
        ProtectionOutcome.Partial => ProtectionState.Partial,
        ProtectionOutcome.Conflict => ProtectionState.Conflict,
        ProtectionOutcome.ProviderUnavailable => ProtectionState.Unavailable,
        ProtectionOutcome.PermissionDenied or ProtectionOutcome.Failed => ProtectionState.Failed,
        _ => ProtectionState.Failed
    };

    private ProtectionOperationRecord SetRecordState(ProtectionOperationRecord record, ProtectionState next, ProtectionFailureCode failure)
    {
        var updated = ProtectionStateMachine.Transition(record, next, failure);
        _journal.Update(updated);
        return updated;
    }

    private ProtectionResult AuditResult(
        ProtectionRequest request,
        string operation,
        ProtectionOutcome outcome,
        ProtectionState state,
        ProtectionFailureCode failure,
        int appliedCount = 0,
        int removedCount = 0,
        Guid? ownerId = null,
        string? policyFingerprint = null)
    {
        _audit.Write(new ProtectionAuditEvent(request.RequestId, request.SessionId, request.ProviderKind,
            operation, outcome, failure, appliedCount + removedCount));
        return new ProtectionResult(outcome, state, failure, appliedCount, removedCount, ownerId, policyFingerprint);
    }

    private ProtectionResult AuditResult(
        ProtectionOperationRecord record,
        string operation,
        ProtectionOutcome outcome,
        ProtectionState state,
        ProtectionFailureCode failure,
        int affectedCount)
    {
        _audit.Write(new ProtectionAuditEvent(record.RequestId, record.SessionId, record.ProviderKind,
            operation, outcome, failure, affectedCount));
        return new ProtectionResult(outcome, state, failure, operation == "apply" ? affectedCount : 0,
            operation == "restore" ? affectedCount : 0, record.OwnerId);
    }

    private static ProtectionResult Result(ProtectionOutcome outcome, ProtectionState state, ProtectionFailureCode failure, Guid? ownerId = null) =>
        new(outcome, state, failure, 0, 0, ownerId);

    private static ProtectionResult Aggregate(IReadOnlyList<ProtectionResult> results)
    {
        if (results.Any(r => r.Outcome == ProtectionOutcome.Conflict))
            return new ProtectionResult(ProtectionOutcome.Conflict, ProtectionState.Conflict,
                results.First(r => r.Outcome == ProtectionOutcome.Conflict).FailureCode,
                results.Sum(r => r.AppliedRuleCount), results.Sum(r => r.RemovedRuleCount));
        if (results.Any(r => r.Outcome == ProtectionOutcome.ProviderUnavailable))
            return new ProtectionResult(ProtectionOutcome.ProviderUnavailable, ProtectionState.Unavailable,
                ProtectionFailureCode.ProviderUnavailable, results.Sum(r => r.AppliedRuleCount), results.Sum(r => r.RemovedRuleCount));
        if (results.Any(r => r.Outcome is ProtectionOutcome.PermissionDenied or ProtectionOutcome.Failed))
            return new ProtectionResult(ProtectionOutcome.Failed, ProtectionState.Failed,
                results.First(r => r.Outcome is ProtectionOutcome.PermissionDenied or ProtectionOutcome.Failed).FailureCode,
                results.Sum(r => r.AppliedRuleCount), results.Sum(r => r.RemovedRuleCount));
        if (results.Any(r => r.Outcome == ProtectionOutcome.Restored))
            return new ProtectionResult(ProtectionOutcome.Restored, ProtectionState.Restored, ProtectionFailureCode.None,
                results.Sum(r => r.AppliedRuleCount), results.Sum(r => r.RemovedRuleCount));
        return new ProtectionResult(ProtectionOutcome.NoOp, ProtectionState.Restored, ProtectionFailureCode.None,
            results.Sum(r => r.AppliedRuleCount), results.Sum(r => r.RemovedRuleCount));
    }
}
