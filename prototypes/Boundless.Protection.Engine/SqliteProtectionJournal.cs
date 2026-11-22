using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Boundless.Protection.Engine;

/// <summary>
/// Durable SQLite implementation of the simulation journal. Sensitive policy identities
/// are retained in the journal for exact ownership checks and recovery; they are never
/// copied to the audit table.
/// </summary>
public sealed class SqliteProtectionJournal : IProtectionJournal, IProtectionCrossProcessLock
{
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 5_000;
    private readonly string _connectionString;
    private readonly string _operationLockPath;
    private readonly object _initializationGate = new();
    private bool _initialized;

    public SqliteProtectionJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        _operationLockPath = fullPath + ".bpe-operation.lock";
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(BusyTimeoutMilliseconds / 1000d)
        }.ToString();
        Initialize();
    }

    /// <summary>
    /// Acquires an exclusive byte-range lock on a sibling lock file. Local Windows
    /// filesystems release the handle lock when a process exits; network filesystems
    /// are outside this prototype's locking guarantee.
    /// </summary>
    public IDisposable AcquireOperationLock(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The simulation's file-region lock is supported on Windows and Linux local filesystems only.");
        Directory.CreateDirectory(Path.GetDirectoryName(_operationLockPath)!);
        var stream = new FileStream(_operationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(BusyTimeoutMilliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    stream.Lock(0, 1);
                    return new FileOperationLock(stream);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    cancellationToken.WaitHandle.WaitOne(50);
                }
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class FileOperationLock(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;
        public void Dispose()
        {
            var active = Interlocked.Exchange(ref _stream, null);
            if (active is null) return;
            try
            {
                if (!OperatingSystem.IsMacOS()) active.Unlock(0, 1);
            }
            finally { active.Dispose(); }
        }
    }

    public ProtectionOperationRecord? Find(Guid requestId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id, session_id, session_generation, owner_id, provider_kind, state, failure_code, payload_fingerprint, baseline_version, baseline_fingerprint, record_json FROM protection_operations WHERE request_id = $id;";
        command.Parameters.AddWithValue("$id", requestId.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var record = Deserialize<ProtectionOperationRecord>(reader.GetString(10), "operation record");
        ValidateIndexedColumns(reader, record);
        return record;
    }

    public bool TryAddPrepared(ProtectionOperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO protection_operations
                (request_id, session_id, session_generation, owner_id, provider_kind, state, failure_code, payload_fingerprint, baseline_version, baseline_fingerprint, record_json)
            VALUES ($request, $session, $generation, $owner, $provider, $state, $failure, $payload, $baseline_version, $baseline_fingerprint, $json);
            SELECT changes();
            """;
        BindRecord(command, record);
        var inserted = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        transaction.Commit();
        return inserted;
    }

    public void Update(ProtectionOperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE protection_operations SET
                session_id = $session,
                session_generation = $generation,
                owner_id = $owner,
                state = $state,
                failure_code = $failure,
                payload_fingerprint = $payload,
                baseline_version = $baseline_version,
                baseline_fingerprint = $baseline_fingerprint,
                record_json = $json
            WHERE request_id = $request;
            """;
        BindRecord(command, record);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Cannot update an unknown protection journal entry.");
        transaction.Commit();
    }

    public IReadOnlyList<ProtectionOperationRecord> ForSession(Guid sessionId, ProtectionProviderKind providerKind) =>
        ReadMany("SELECT request_id, session_id, session_generation, owner_id, provider_kind, state, failure_code, payload_fingerprint, baseline_version, baseline_fingerprint, record_json FROM protection_operations WHERE session_id = $session AND provider_kind = $provider ORDER BY rowid;",
            command =>
            {
                command.Parameters.AddWithValue("$session", sessionId.ToString("N"));
                command.Parameters.AddWithValue("$provider", providerKind.ToString());
            });

    public IReadOnlyList<ProtectionOperationRecord> Recoverable() =>
        ReadMany("SELECT request_id, session_id, session_generation, owner_id, provider_kind, state, failure_code, payload_fingerprint, baseline_version, baseline_fingerprint, record_json FROM protection_operations WHERE state NOT IN ('Restored') ORDER BY rowid;", _ => { });

    public IReadOnlyList<ProtectionOperationRecord> All() =>
        ReadMany("SELECT request_id, session_id, session_generation, owner_id, provider_kind, state, failure_code, payload_fingerprint, baseline_version, baseline_fingerprint, record_json FROM protection_operations ORDER BY rowid;", _ => { });

    private IReadOnlyList<ProtectionOperationRecord> ReadMany(string sql, Action<SqliteCommand> bind)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        using var reader = command.ExecuteReader();
        var records = new List<ProtectionOperationRecord>();
        while (reader.Read())
        {
            var record = Deserialize<ProtectionOperationRecord>(reader.GetString(10), "operation record");
            ValidateIndexedColumns(reader, record);
            records.Add(record);
        }
        return records.AsReadOnly();
    }

    private static void ValidateIndexedColumns(SqliteDataReader row, ProtectionOperationRecord record)
    {
        var consistent = row.GetString(0) == record.RequestId.ToString("N")
            && row.GetString(1) == record.SessionId.ToString("N")
            && row.GetInt64(2) == record.SessionGeneration
            && row.GetString(3) == record.OwnerId.ToString("N")
            && row.GetString(4) == record.ProviderKind.ToString()
            && row.GetString(5) == record.State.ToString()
            && row.GetString(6) == record.LastFailure.ToString()
            && row.GetString(7) == record.PayloadFingerprint
            && row.GetInt64(8) == record.Baseline.Version
            && row.GetString(9) == record.Baseline.Fingerprint;
        if (!consistent)
            throw new InvalidDataException("Protection journal indexed columns disagree with the stored operation record; data was preserved.");
    }

    private SqliteConnection Open()
    {
        EnsureInitialized();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initializationGate)
        {
            if (_initialized) return;
            Initialize();
        }
    }

    private void Initialize()
    {
        lock (_initializationGate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
                pragmas.ExecuteNonQuery();
            }

            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = integrity.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Protection journal failed SQLite integrity_check.");
            }

            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                var current = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (current > SchemaVersion)
                    throw new InvalidDataException($"Protection journal schema {current} is newer than supported schema {SchemaVersion}.");
                if (current == 0)
                {
                    using var migration = connection.CreateCommand();
                    migration.CommandText = """
                        BEGIN IMMEDIATE;
                        CREATE TABLE protection_operations (
                            request_id TEXT PRIMARY KEY NOT NULL,
                            session_id TEXT NOT NULL,
                            session_generation INTEGER NOT NULL,
                            owner_id TEXT NOT NULL,
                            provider_kind TEXT NOT NULL,
                            state TEXT NOT NULL,
                            failure_code TEXT NOT NULL,
                            payload_fingerprint TEXT NOT NULL,
                            baseline_version INTEGER NOT NULL,
                            baseline_fingerprint TEXT NOT NULL,
                            record_json TEXT NOT NULL
                        );
                        CREATE INDEX ix_protection_operations_session_provider ON protection_operations(session_id, provider_kind);
                        PRAGMA user_version = 1;
                        COMMIT;
                        """;
                    migration.ExecuteNonQuery();
                }
            }
            _initialized = true;
        }
    }

    private static void BindRecord(SqliteCommand command, ProtectionOperationRecord record)
    {
        command.Parameters.AddWithValue("$request", record.RequestId.ToString("N"));
        command.Parameters.AddWithValue("$session", record.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$generation", record.SessionGeneration);
        command.Parameters.AddWithValue("$owner", record.OwnerId.ToString("N"));
        command.Parameters.AddWithValue("$provider", record.ProviderKind.ToString());
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$failure", record.LastFailure.ToString());
        command.Parameters.AddWithValue("$payload", record.PayloadFingerprint);
        command.Parameters.AddWithValue("$baseline_version", record.Baseline.Version);
        command.Parameters.AddWithValue("$baseline_fingerprint", record.Baseline.Fingerprint);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record));
    }

    private static T Deserialize<T>(string json, string description)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidDataException($"Protection journal {description} is empty.");
            if (value is ProtectionOperationRecord record) ValidateRecord(record);
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Protection journal {description} is malformed; records were preserved.", exception);
        }
    }

    private static void ValidateRecord(ProtectionOperationRecord record)
    {
        if (record.RequestId == Guid.Empty || record.SessionId == Guid.Empty || record.OwnerId == Guid.Empty
            || record.SessionGeneration <= 0 || string.IsNullOrWhiteSpace(record.PayloadFingerprint)
            || !Enum.IsDefined(record.ProviderKind) || !Enum.IsDefined(record.State) || !Enum.IsDefined(record.LastFailure)
            || record.Baseline is null || record.RequestedRules is null || record.AppliedRules is null
            || record.Baseline.ProviderKind != record.ProviderKind)
            throw new InvalidDataException("Protection journal record failed semantic validation; stored data was preserved.");

        if (record.RequestedRules.Count == 0 || record.RequestedRules.Any(rule => rule is null
            || rule.RuleId == Guid.Empty || !string.Equals(rule.SubjectSid, record.SubjectSid, StringComparison.Ordinal)
            || rule.Identity is null || !rule.Identity.IsValid)
            || record.RequestedRules.Select(rule => rule.RuleId).Distinct().Count() != record.RequestedRules.Count)
            throw new InvalidDataException("Protection journal requested rules failed semantic validation; stored data was preserved.");

        var intended = record.RequestedRules.ToDictionary(rule => rule.RuleId);
        if (record.AppliedRules.Any(rule => rule is null || rule.OwnerId != record.OwnerId
            || !intended.TryGetValue(rule.RuleId, out var requested)
            || !string.Equals(rule.SubjectSid, requested.SubjectSid, StringComparison.Ordinal)
            || rule.Identity != requested.Identity))
            throw new InvalidDataException("Protection journal applied rules failed ownership validation; stored data was preserved.");

        if (record.Baseline.Rules is null || record.Baseline.Rules.Any(rule => rule is null
            || rule.RuleId == Guid.Empty || rule.SubjectSid is null || rule.Identity is null || !rule.Identity.IsValid))
            throw new InvalidDataException("Protection journal baseline failed semantic validation; stored data was preserved.");
    }
}

/// <summary>Append-only SQLite audit for the simulation. This is not tamper-proof.</summary>
public sealed class SqliteProtectionAuditSink : IProtectionAuditSink
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;

    public SqliteProtectionAuditSink(string databasePath, int busyTimeoutMilliseconds = 5_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds is < 1 or > 60_000) throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(busyTimeoutMilliseconds / 1000d)
        }.ToString();
        Initialize();
    }

    public void Write(ProtectionAuditEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO protection_audit_events
                (request_id, session_id, provider_kind, operation, outcome, failure_code, affected_rule_count, timestamp_utc)
            VALUES ($request, $session, $provider, $operation, $outcome, $failure, $count, $timestamp);
            """;
        command.Parameters.AddWithValue("$request", entry.RequestId.ToString("N"));
        command.Parameters.AddWithValue("$session", entry.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$provider", entry.ProviderKind.ToString());
        command.Parameters.AddWithValue("$operation", entry.Operation);
        command.Parameters.AddWithValue("$outcome", entry.Outcome.ToString());
        command.Parameters.AddWithValue("$failure", entry.FailureCode.ToString());
        command.Parameters.AddWithValue("$count", entry.AffectedRuleCount);
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<(ProtectionAuditEvent Event, DateTimeOffset Timestamp)> ReadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id, session_id, provider_kind, operation, outcome, failure_code, affected_rule_count, timestamp_utc FROM protection_audit_events ORDER BY event_id;";
        using var reader = command.ExecuteReader();
        var entries = new List<(ProtectionAuditEvent, DateTimeOffset)>();
        while (reader.Read())
        {
            if (!Enum.TryParse<ProtectionProviderKind>(reader.GetString(2), out var provider)
                || !Enum.TryParse<ProtectionOutcome>(reader.GetString(4), out var outcome)
                || !Enum.TryParse<ProtectionFailureCode>(reader.GetString(5), out var failure)
                || !DateTimeOffset.TryParse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                throw new InvalidDataException("Protection audit contains malformed data.");
            entries.Add((new ProtectionAuditEvent(Guid.ParseExact(reader.GetString(0), "N"), Guid.ParseExact(reader.GetString(1), "N"), provider,
                reader.GetString(3), outcome, failure, reader.GetInt32(6)), timestamp));
        }
        return entries.AsReadOnly();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}; PRAGMA synchronous = FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}; PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
            pragmas.ExecuteNonQuery();
        }
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Protection audit failed SQLite integrity_check.");
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (current > 1) throw new InvalidDataException($"Protection audit schema {current} is newer than supported schema 1.");
            if (current == 0)
            {
                using var migration = connection.CreateCommand();
                migration.CommandText = "BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS protection_audit_events (event_id INTEGER PRIMARY KEY AUTOINCREMENT, request_id TEXT NOT NULL, session_id TEXT NOT NULL, provider_kind TEXT NOT NULL, operation TEXT NOT NULL, outcome TEXT NOT NULL, failure_code TEXT NOT NULL, affected_rule_count INTEGER NOT NULL, timestamp_utc TEXT NOT NULL); PRAGMA user_version = 1; COMMIT;";
                migration.ExecuteNonQuery();
            }
        }
    }
}
