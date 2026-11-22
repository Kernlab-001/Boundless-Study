using System.Text.Json;
using Boundless.Protection.Engine;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class DurableStoreTests
{
    private const string Sid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task JournalProviderAndAudit_ReopenWithPersistentSyntheticStateAndRedactedAppendOnlyEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "policy.db");
            var auditPath = Path.Combine(directory, "audit.db");
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.Wfp);
            var journal = new SqliteProtectionJournal(journalPath);
            var audit = new SqliteProtectionAuditSink(auditPath);
            var request = new ProtectionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, Sid,
                ProtectionProviderKind.Wfp,
                [new RequestedRule(Guid.NewGuid(), Sid, new ApplicationIdentity("C:\\BPE-Synthetic\\private.exe", Hash))]);

            var applied = await new ProtectionEngine([provider], journal, audit).ApplyAsync(request);
            Assert.Equal(ProtectionOutcome.Applied, applied.Outcome);
            provider.AddExternalRule(new ProtectionRule(Guid.NewGuid(), Guid.NewGuid(), Sid,
                new ApplicationIdentity("C:\\BPE-Synthetic\\external.exe", new string('b', 64))));
            provider.ModifyRuleExternally(request.Rules[0].RuleId);

            var reopenedJournal = new SqliteProtectionJournal(journalPath);
            var reopenedProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.Wfp);
            var reopenedAudit = new SqliteProtectionAuditSink(auditPath);
            Assert.Equal(ProtectionState.Active, reopenedJournal.Find(request.RequestId)!.State);
            Assert.Equal(2, (await reopenedProvider.SnapshotAsync()).Rules.Count);

            var auditRowsBefore = reopenedAudit.ReadAll();
            Assert.Single(auditRowsBefore);
            Assert.True(auditRowsBefore[0].Timestamp.Offset == TimeSpan.Zero);
            reopenedAudit.Write(new ProtectionAuditEvent(request.RequestId, request.SessionId, request.ProviderKind,
                "restore", ProtectionOutcome.Conflict, ProtectionFailureCode.OwnershipMismatch, 1));
            var auditJson = JsonSerializer.Serialize(new SqliteProtectionAuditSink(auditPath).ReadAll());
            Assert.Equal(2, new SqliteProtectionAuditSink(auditPath).ReadAll().Count);
            Assert.DoesNotContain(Sid, auditJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private.exe", auditJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Hash, auditJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
