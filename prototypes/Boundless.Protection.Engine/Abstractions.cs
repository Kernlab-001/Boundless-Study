namespace Boundless.Protection.Engine;

public interface IProtectionProvider
{
    ProtectionProviderKind Kind { get; }
    ProtectionProviderKind ProviderKind => Kind;
    Task<PolicySnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default);
    Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactRules, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default);
}

public interface IProtectionJournal
{
    ProtectionOperationRecord? Find(Guid requestId);
    bool TryAddPrepared(ProtectionOperationRecord record);
    void Update(ProtectionOperationRecord record);
    IReadOnlyList<ProtectionOperationRecord> ForSession(Guid sessionId, ProtectionProviderKind providerKind);
    IReadOnlyList<ProtectionOperationRecord> Recoverable();
    IReadOnlyList<ProtectionOperationRecord> All();
}

/// <summary>
/// Optional process-wide operation gate for durable journals. Implementations must
/// release the lock when the owning process exits and bound acquisition time.
/// </summary>
public interface IProtectionCrossProcessLock
{
    IDisposable AcquireOperationLock(CancellationToken cancellationToken = default);
}

public interface IProtectionAuditSink
{
    void Write(ProtectionAuditEvent entry);
}

internal static class ProtectionAuditValidation
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "apply", "restore", "recover"
    };

    public static void Validate(ProtectionAuditEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.RequestId == Guid.Empty || entry.SessionId == Guid.Empty
            || !Enum.IsDefined(entry.ProviderKind) || !Enum.IsDefined(entry.Outcome)
            || !Enum.IsDefined(entry.FailureCode) || entry.AffectedRuleCount < 0
            || !AllowedOperations.Contains(entry.Operation))
            throw new ArgumentException("Audit event contains invalid or non-allowlisted fields.", nameof(entry));
    }
}

public sealed class InMemoryProtectionAuditSink : IProtectionAuditSink
{
    private readonly object _gate = new();
    private readonly List<ProtectionAuditEvent> _entries = [];

    public IReadOnlyList<ProtectionAuditEvent> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Write(ProtectionAuditEvent entry)
    {
        ProtectionAuditValidation.Validate(entry);
        lock (_gate) _entries.Add(entry);
    }
}

public sealed class ProtectionJournal : IProtectionJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProtectionOperationRecord> _records = [];

    public ProtectionOperationRecord? Find(Guid requestId)
    {
        lock (_gate) return _records.GetValueOrDefault(requestId);
    }

    public bool TryAddPrepared(ProtectionOperationRecord record)
    {
        lock (_gate) return _records.TryAdd(record.RequestId, record);
    }

    public void Update(ProtectionOperationRecord record)
    {
        lock (_gate)
        {
            if (!_records.ContainsKey(record.RequestId))
                throw new InvalidOperationException("Cannot update an unknown protection journal entry.");
            _records[record.RequestId] = record;
        }
    }

    public IReadOnlyList<ProtectionOperationRecord> ForSession(Guid sessionId, ProtectionProviderKind providerKind)
    {
        lock (_gate) return _records.Values.Where(r => r.SessionId == sessionId && r.ProviderKind == providerKind).ToArray();
    }

    public IReadOnlyList<ProtectionOperationRecord> Recoverable()
    {
        lock (_gate) return _records.Values.Where(r => r.State is not (ProtectionState.Restored or ProtectionState.Conflict)).ToArray();
    }

    public IReadOnlyList<ProtectionOperationRecord> All()
    {
        lock (_gate) return _records.Values.ToArray();
    }
}
