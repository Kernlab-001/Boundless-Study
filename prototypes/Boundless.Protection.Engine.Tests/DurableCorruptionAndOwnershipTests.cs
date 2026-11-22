using Boundless.Protection.Engine;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class DurableCorruptionAndOwnershipTests
{
    private const string Sid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CorruptJournalFileIsRejectedWithoutOverwritingOriginalBytes()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "journal.db");
            byte[] original = [0x42, 0x50, 0x45, 0x2D, 0x43, 0x4F, 0x52, 0x52, 0x55, 0x50, 0x54];
            File.WriteAllBytes(path, original);

            Assert.ThrowsAny<Exception>(() => new SqliteProtectionJournal(path));

            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task MalformedJournalRecordIsRejectedAndPreserved()
    {
        var directory = NewDirectory();
        var journalPath = Path.Combine(directory, "journal.db");
        try
        {
            var journal = new SqliteProtectionJournal(journalPath);
            var provider = new SimulatedAppLockerProvider();
            var request = Request(provider.Kind,
                new RequestedRule(Guid.NewGuid(), Sid, new ApplicationIdentity("C:\\BPE-Synthetic\\journal.exe", Hash)));
            Assert.Equal(ProtectionOutcome.Applied,
                (await new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink()).ApplyAsync(request)).Outcome);

            using (var connection = Open(journalPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE protection_operations SET record_json = '{broken-json' WHERE request_id = $id;";
                command.Parameters.AddWithValue("$id", request.RequestId.ToString("N"));
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var reopened = new SqliteProtectionJournal(journalPath);
            Assert.Throws<InvalidDataException>(() => reopened.All());
            using var verifyConnection = Open(journalPath);
            using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT record_json FROM protection_operations WHERE request_id = $id;";
            verifyCommand.Parameters.AddWithValue("$id", request.RequestId.ToString("N"));
            Assert.Equal("{broken-json", verifyCommand.ExecuteScalar());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void UnsupportedFutureJournalSchemaIsRejectedWithoutDeletingRecords()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "journal.db");
        try
        {
            _ = new SqliteProtectionJournal(path);
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE preserved_marker (value TEXT NOT NULL); INSERT INTO preserved_marker VALUES ('keep'); PRAGMA user_version = 999;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => new SqliteProtectionJournal(path));
            using var verifyConnection = Open(path);
            using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT value FROM preserved_marker;";
            Assert.Equal("keep", verifyCommand.ExecuteScalar());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void InvalidAuditEventIsRejectedBeforeAppend()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "audit.db");
        try
        {
            var sink = new SqliteProtectionAuditSink(path);
            var invalid = new ProtectionAuditEvent(Guid.Empty, Guid.NewGuid(), ProtectionProviderKind.AppLocker,
                "apply", ProtectionOutcome.Applied, ProtectionFailureCode.None, 1);

            Assert.Throws<ArgumentException>(() => sink.Write(invalid));
            Assert.Empty(sink.ReadAll());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void MalformedPersistedAuditFieldsAreRejected()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "audit.db");
        try
        {
            var sink = new SqliteProtectionAuditSink(path);
            sink.Write(new ProtectionAuditEvent(Guid.NewGuid(), Guid.NewGuid(), ProtectionProviderKind.AppLocker,
                "apply", ProtectionOutcome.Applied, ProtectionFailureCode.None, 1));

            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE protection_audit_events SET outcome = 'UnknownOutcome';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            Assert.Throws<InvalidDataException>(() => new SqliteProtectionAuditSink(path).ReadAll());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void ValidAuditRowEditsAreNotCryptographicallyDetected()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "audit.db");
        try
        {
            var sink = new SqliteProtectionAuditSink(path);
            var id = Guid.NewGuid();
            sink.Write(new ProtectionAuditEvent(id, Guid.NewGuid(), ProtectionProviderKind.AppLocker,
                "apply", ProtectionOutcome.Applied, ProtectionFailureCode.None, 1));

            // This test documents the audit store's limitation: valid-value edits are
            // readable; the SQLite audit trail is append-oriented but not tamper-proof.
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE protection_audit_events SET outcome = 'Failed';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            Assert.Equal(ProtectionOutcome.Failed, Assert.Single(new SqliteProtectionAuditSink(path).ReadAll()).Event.Outcome);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task DurableRestorePreservesExternalChangesAndIsIdempotentAfterConflictResolution()
    {
        var directory = NewDirectory();
        var journalPath = Path.Combine(directory, "journal.db");
        var providerPath = Path.Combine(directory, "synthetic-policy.db");
        try
        {
            var journal = new SqliteProtectionJournal(journalPath);
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.Wfp);
            var baseline = new ProtectionRule(Guid.NewGuid(), Guid.NewGuid(), Sid,
                new ApplicationIdentity("C:\\BPE-Synthetic\\admin-rule.exe", new string('b', 64)));
            provider.AddExternalRule(baseline);
            var request = Request(ProtectionProviderKind.Wfp,
                new RequestedRule(Guid.NewGuid(), Sid, new ApplicationIdentity("C:\\BPE-Synthetic\\owned-one.exe", Hash)),
                new RequestedRule(Guid.NewGuid(), Sid, new ApplicationIdentity("C:\\BPE-Synthetic\\owned-two.exe", new string('c', 64))));
            var engine = new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink());
            Assert.Equal(ProtectionOutcome.Applied, (await engine.ApplyAsync(request)).Outcome);

            provider.ModifyRuleExternally(request.Rules[1].RuleId);
            Assert.Equal(ProtectionOutcome.Conflict,
                (await engine.RestoreAsync(request.SessionId, request.ProviderKind)).Outcome);
            var afterConflict = (await provider.SnapshotAsync()).Rules;
            Assert.Equal(3, afterConflict.Count);
            Assert.Contains(afterConflict, rule => rule == baseline);

            provider.RemoveRuleExternally(request.Rules[1].RuleId);
            var restored = await engine.RestoreAsync(request.SessionId, request.ProviderKind);
            Assert.Equal(ProtectionOutcome.Restored, restored.Outcome);
            var afterRestore = Assert.Single((await provider.SnapshotAsync()).Rules);
            Assert.Equal(baseline, afterRestore);
            Assert.Equal(ProtectionState.Restored, journal.Find(request.RequestId)!.State);

            var repeated = await new ProtectionEngine(
                [new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.Wfp)],
                new SqliteProtectionJournal(journalPath), new InMemoryProtectionAuditSink())
                .RestoreAsync(request.SessionId, request.ProviderKind);
            Assert.Equal(ProtectionOutcome.NoOp, repeated.Outcome);
            Assert.Equal(baseline, Assert.Single((await provider.SnapshotAsync()).Rules));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static ProtectionRequest Request(ProtectionProviderKind kind, params RequestedRule[] rules) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, Sid, kind, rules);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Corruption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string directory)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
}
