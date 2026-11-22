using Microsoft.Data.Sqlite;

namespace Boundless.Protection.Engine;

/// <summary>
/// SQLite-backed synthetic policy store. It models AppLocker/WFP-shaped policy state
/// and never calls operating-system policy APIs.
/// </summary>
public sealed class PersistentSimulatedPolicyProvider : IProtectionProvider
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _databasePath;
    private readonly int _busyTimeoutMilliseconds;
    private readonly object _injectionGate = new();
    private bool _available = true;
    private bool _denyWrites;
    private bool _failRestore;
    private int? _failApplyAfter;
    private int? _crashApplyAfter;
    private int? _crashRestoreAfter;

    public PersistentSimulatedPolicyProvider(string databasePath, ProtectionProviderKind kind, int busyTimeoutMilliseconds = 5000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds is < 1 or > 60_000) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        _databasePath = Path.GetFullPath(databasePath);
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        Kind = kind;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        InitializeAndCheck();
    }

    public ProtectionProviderKind Kind { get; }
    public ProtectionProviderKind ProviderKind => Kind;

    public void SetAvailable(bool value) { lock (_injectionGate) _available = value; }
    public void DenyWrites(bool value) { lock (_injectionGate) _denyWrites = value; }
    public void FailRestore(bool value) { lock (_injectionGate) _failRestore = value; }
    public void FailApplyAfter(int count) { lock (_injectionGate) _failApplyAfter = Math.Max(0, count); }
    public void CrashApplyAfter(int count) { lock (_injectionGate) _crashApplyAfter = Math.Max(0, count); }
    public void CrashRestoreAfter(int count) { lock (_injectionGate) _crashRestoreAfter = Math.Max(0, count); }

    public Task<PolicySnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PolicyVersion FROM Metadata WHERE Id = 1;";
        var version = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = new PolicySnapshot(Kind, version, ReadRules(connection, transaction));
        transaction.Commit();
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        using var connection = Open();
        IReadOnlyList<ProtectionRule> rules = ReadRules(connection).Where(r => r.OwnerId == ownerId).OrderBy(r => r.RuleId).ToArray();
        return Task.FromResult(rules);
    }

    public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryWrite(out var injectedFailure)) return Task.FromResult(injectedFailure);

        var crashAfter = TakeApplyInjection(crash: true);
        var failAfter = TakeApplyInjection(crash: false);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var currentVersion = ReadVersion(connection, transaction);
        var storedRules = ReadRules(connection, transaction);
        var current = new PolicySnapshot(Kind, currentVersion, storedRules);
        if (current.Version != expectedVersion || !string.Equals(current.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            transaction.Rollback();
            return Task.FromResult(Result(ProtectionOutcome.Conflict, ProtectionFailureCode.PolicyDrift));
        }
        if (rules.Any(r => storedRules.Any(s => s.RuleId == r.RuleId)))
        {
            transaction.Rollback();
            return Task.FromResult(Result(ProtectionOutcome.Conflict, ProtectionFailureCode.RuleIdConflict));
        }

        var injectionCount = crashAfter ?? failAfter;
        var count = Math.Min(rules.Count, injectionCount ?? rules.Count);
        var added = new List<ProtectionRule>(count);
        foreach (var requested in rules.Take(count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = new ProtectionRule(requested.RuleId, ownerId, requested.SubjectSid, requested.Identity);
            InsertRule(connection, transaction, rule);
            added.Add(rule);
        }
        if (count > 0) IncrementVersion(connection, transaction, count);
        transaction.Commit();

        // Commit before throwing: a fresh process can observe durable partial/full state.
        if (crashAfter.HasValue) throw new SimulatedApplicationCrashException();
        if (failAfter.HasValue && count < rules.Count)
        {
            var outcome = count == 0 ? ProtectionOutcome.Failed : ProtectionOutcome.Partial;
            return Task.FromResult(new ProviderMutationResult(outcome, ProtectionFailureCode.PartialApplication, added));
        }
        return Task.FromResult(new ProviderMutationResult(ProtectionOutcome.Applied, ProtectionFailureCode.None, added));
    }

    public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactRules, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int? crashAfter;
        lock (_injectionGate)
        {
            if (!_available) return Task.FromResult(Result(ProtectionOutcome.ProviderUnavailable, ProtectionFailureCode.ProviderUnavailable));
            if (_denyWrites) return Task.FromResult(Result(ProtectionOutcome.PermissionDenied, ProtectionFailureCode.PermissionDenied));
            if (_failRestore) return Task.FromResult(Result(ProtectionOutcome.Failed, ProtectionFailureCode.RestoreFailure));
            crashAfter = _crashRestoreAfter;
            _crashRestoreAfter = null;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var currentRules = ReadRules(connection, transaction).ToDictionary(r => r.RuleId);
        var conflict = exactRules.Any(expected => currentRules.TryGetValue(expected.RuleId, out var actual)
            && (actual.OwnerId != ownerId || actual != expected));
        if (conflict)
        {
            transaction.Rollback();
            return Task.FromResult(Result(ProtectionOutcome.Conflict, ProtectionFailureCode.OwnershipMismatch));
        }

        var removed = new List<ProtectionRule>();
        foreach (var expected in exactRules.Take(crashAfter ?? exactRules.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!currentRules.TryGetValue(expected.RuleId, out var actual)) continue;
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Rules WHERE RuleId = $id AND OwnerId = $owner;";
            delete.Parameters.AddWithValue("$id", expected.RuleId.ToString("D"));
            delete.Parameters.AddWithValue("$owner", ownerId.ToString("D"));
            if (delete.ExecuteNonQuery() == 1) removed.Add(actual);
        }
        if (removed.Count > 0) IncrementVersion(connection, transaction, removed.Count);
        transaction.Commit();
        if (crashAfter.HasValue) throw new SimulatedApplicationCrashException();
        var result = removed.Count == 0 ? ProtectionOutcome.NoOp : ProtectionOutcome.Restored;
        return Task.FromResult(new ProviderMutationResult(result, ProtectionFailureCode.None, removed));
    }

    public void AddExternalRule(ProtectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.RuleId == Guid.Empty || rule.SubjectSid is null || rule.Identity is null || !rule.Identity.IsValid)
            throw new ArgumentException("Synthetic external rules must have valid identities.", nameof(rule));
        EnsureAvailable();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        try { InsertRule(connection, transaction, rule); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            transaction.Rollback();
            throw new InvalidOperationException("Rule ID already exists in the synthetic store.", ex);
        }
        IncrementVersion(connection, transaction, 1);
        transaction.Commit();
    }

    public void ModifyRuleExternally(Guid ruleId)
    {
        EnsureAvailable();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Rules SET OwnerId = $external, Sha256 = $hash WHERE RuleId = $id;";
        command.Parameters.AddWithValue("$external", Guid.Empty.ToString("D"));
        command.Parameters.AddWithValue("$hash", new string('F', 64));
        command.Parameters.AddWithValue("$id", ruleId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Rule was not found.");
        IncrementVersion(connection, transaction, 1);
        transaction.Commit();
    }

    public void RemoveRuleExternally(Guid ruleId)
    {
        EnsureAvailable();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Rules WHERE RuleId = $id;";
        command.Parameters.AddWithValue("$id", ruleId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Rule was not found.");
        IncrementVersion(connection, transaction, 1);
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_busyTimeoutMilliseconds / 1000d))
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}; PRAGMA synchronous = FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void InitializeAndCheck()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            if (!string.Equals(Convert.ToString(wal.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Synthetic policy store could not enter WAL mode.");
        }
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(integrity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Synthetic policy store failed SQLite integrity_check.");
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS Metadata (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                SchemaVersion INTEGER NOT NULL,
                PolicyVersion INTEGER NOT NULL CHECK (PolicyVersion >= 0),
                ProviderKind TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Rules (
                RuleId TEXT PRIMARY KEY,
                OwnerId TEXT NOT NULL,
                SubjectSid TEXT NOT NULL,
                ExecutablePath TEXT NOT NULL,
                Sha256 TEXT NOT NULL
            );
            INSERT OR IGNORE INTO Metadata (Id, SchemaVersion, PolicyVersion, ProviderKind)
                VALUES (1, 1, 0, $kind);
            SELECT SchemaVersion, ProviderKind FROM Metadata WHERE Id = 1;
            """;
        schema.Parameters.AddWithValue("$kind", Kind.ToString());
        using var reader = schema.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Synthetic policy store metadata is missing.");
        var schemaVersion = reader.GetInt32(0);
        var providerKind = reader.GetString(1);
        reader.Close();
        if (schemaVersion != CurrentSchemaVersion) throw new InvalidDataException($"Unsupported synthetic policy schema version {schemaVersion}.");
        if (!string.Equals(providerKind, Kind.ToString(), StringComparison.Ordinal)) throw new InvalidDataException("Synthetic policy store provider kind does not match the requested provider.");
        transaction.Commit();
    }

    private static IReadOnlyList<ProtectionRule> ReadRules(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT RuleId, OwnerId, SubjectSid, ExecutablePath, Sha256 FROM Rules ORDER BY RuleId;";
        using var reader = command.ExecuteReader();
        var rules = new List<ProtectionRule>();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var ruleId) || !Guid.TryParse(reader.GetString(1), out var ownerId))
                throw new InvalidDataException("Synthetic policy store contains an invalid rule identity.");
            var identity = new ApplicationIdentity(reader.GetString(3), reader.GetString(4));
            if (!identity.IsValid) throw new InvalidDataException("Synthetic policy store contains an invalid application identity.");
            rules.Add(new ProtectionRule(ruleId, ownerId, reader.GetString(2), identity));
        }
        return rules;
    }

    private static long ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PolicyVersion FROM Metadata WHERE Id = 1;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertRule(SqliteConnection connection, SqliteTransaction transaction, ProtectionRule rule)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Rules (RuleId, OwnerId, SubjectSid, ExecutablePath, Sha256) VALUES ($id, $owner, $sid, $path, $hash);";
        command.Parameters.AddWithValue("$id", rule.RuleId.ToString("D"));
        command.Parameters.AddWithValue("$owner", rule.OwnerId.ToString("D"));
        command.Parameters.AddWithValue("$sid", rule.SubjectSid);
        command.Parameters.AddWithValue("$path", rule.Identity.ExecutablePath);
        command.Parameters.AddWithValue("$hash", rule.Identity.Sha256);
        command.ExecuteNonQuery();
    }

    private static void IncrementVersion(SqliteConnection connection, SqliteTransaction transaction, int amount)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Metadata SET PolicyVersion = PolicyVersion + $amount WHERE Id = 1;";
        command.Parameters.AddWithValue("$amount", amount);
        command.ExecuteNonQuery();
    }

    private bool TryWrite(out ProviderMutationResult result)
    {
        lock (_injectionGate)
        {
            if (!_available) { result = Result(ProtectionOutcome.ProviderUnavailable, ProtectionFailureCode.ProviderUnavailable); return false; }
            if (_denyWrites) { result = Result(ProtectionOutcome.PermissionDenied, ProtectionFailureCode.PermissionDenied); return false; }
            result = null!;
            return true;
        }
    }

    private int? TakeApplyInjection(bool crash)
    {
        lock (_injectionGate)
        {
            ref int? injection = ref (crash ? ref _crashApplyAfter : ref _failApplyAfter);
            var value = injection;
            injection = null;
            return value;
        }
    }

    private void EnsureAvailable()
    {
        lock (_injectionGate)
            if (!_available) throw new ProtectionProviderUnavailableException();
    }

    private static ProviderMutationResult Result(ProtectionOutcome outcome, ProtectionFailureCode code) =>
        new(outcome, code, Array.Empty<ProtectionRule>());
}
