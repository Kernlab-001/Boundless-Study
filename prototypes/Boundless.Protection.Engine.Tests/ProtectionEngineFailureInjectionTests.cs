using System.Text.Json;
using Boundless.Protection.Engine;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class ProtectionEngineFailureInjectionTests
{
    private const string SubjectOne = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string SubjectTwo = "S-1-5-21-111111111-222222222-333333333-1002";
    private const string SyntheticHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task PartialApply_RecoveryRemovesOnlyExperimentOwnedRules()
    {
        var provider = new SimulatedAppLockerProvider();
        var journal = new ProtectionJournal();
        var original = ExternalRule(SubjectTwo, "C:\\BPE-Synthetic\\baseline.exe");
        provider.AddExternalRule(original);
        provider.FailApplyAfter(1);

        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\restricted-one.exe"),
            Requested(SubjectOne, "C:\\BPE-Synthetic\\restricted-two.exe"));

        var firstEngine = Engine(provider, journal);
        var apply = await firstEngine.ApplyAsync(request);

        Assert.Equal("Partial", apply.Outcome.ToString());
        Assert.Single(await provider.GetOwnedRulesAsync(apply.OwnerId!.Value));

        // A new engine instance models process restart; journal and provider are the
        // only surviving state. Recovery must not replay or blindly restore a snapshot.
        var restartedEngine = Engine(provider, journal);
        await restartedEngine.RecoverAsync();

        var remaining = (await provider.SnapshotAsync()).Rules;
        Assert.Contains(remaining, x => x.RuleId == original.RuleId && x.OwnerId == original.OwnerId);
        Assert.DoesNotContain(remaining, x => x.RuleId == request.Rules[0].RuleId);
        Assert.DoesNotContain(remaining, x => x.RuleId == request.Rules[1].RuleId);
    }

    [Fact]
    public async Task SimulatedApplicationCrashDuringApply_IsRecoveredAfterEngineRestart()
    {
        var provider = new SimulatedAppLockerProvider();
        var journal = new ProtectionJournal();
        provider.CrashApplyAfter(1);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\crash-one.exe"),
            Requested(SubjectOne, "C:\\BPE-Synthetic\\crash-two.exe"));

        var firstEngine = Engine(provider, journal);
        await Assert.ThrowsAsync<SimulatedApplicationCrashException>(() => firstEngine.ApplyAsync(request));
        Assert.Equal(ProtectionState.Applying, journal.Find(request.RequestId)!.State);
        Assert.NotEmpty((await provider.SnapshotAsync()).Rules);

        var restartedEngine = Engine(provider, journal);
        await restartedEngine.RecoverAsync();

        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task ProviderDisconnectionDuringRestore_IsReportedAndCanRecoverAfterReconnect()
    {
        var provider = new SimulatedWfpProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\network-target.exe"));

        Assert.Equal("Applied", (await engine.ApplyAsync(request)).Outcome.ToString());
        provider.SetAvailable(false);

        var disconnected = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);
        Assert.Equal("ProviderUnavailable", disconnected.Outcome.ToString());

        provider.SetAvailable(true);
        Assert.Contains((await provider.SnapshotAsync()).Rules, x => x.RuleId == request.Rules[0].RuleId);
        var recovered = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);

        Assert.Equal("Restored", recovered.Outcome.ToString());
        Assert.DoesNotContain((await provider.SnapshotAsync()).Rules, x => x.RuleId == request.Rules[0].RuleId);
    }

    [Fact]
    public async Task PermissionFailure_DoesNotMutateProviderOrClaimEnforcement()
    {
        var provider = new SimulatedAppLockerProvider();
        provider.DenyWrites(true);
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\permission-denied.exe"));

        var result = await engine.ApplyAsync(request);

        Assert.NotEqual("Applied", result.Outcome.ToString());
        Assert.Contains("Permission", result.FailureCode.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task ProviderUnavailableDuringApply_DoesNotClaimEnforcement()
    {
        var provider = new SimulatedWfpProvider();
        provider.SetAvailable(false);
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\provider-unavailable.exe"));

        var result = await engine.ApplyAsync(request);

        Assert.Equal("ProviderUnavailable", result.Outcome.ToString());
        provider.SetAvailable(true);
        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task DuplicateRequest_IsIdempotent_ButSameIdWithDifferentPayloadConflicts()
    {
        var provider = new SimulatedAppLockerProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\reader.exe"));

        var first = await engine.ApplyAsync(request);
        var duplicate = await engine.ApplyAsync(request);
        var conflicting = request with
        {
            Rules = [Requested(SubjectOne, "C:\\BPE-Synthetic\\different.exe")]
        };
        var conflict = await engine.ApplyAsync(conflicting);

        Assert.Equal("Applied", first.Outcome.ToString());
        Assert.Equal("Duplicate", duplicate.Outcome.ToString());
        Assert.Equal("Conflict", conflict.Outcome.ToString());
        Assert.Single((await provider.SnapshotAsync()).Rules, x => x.OwnerId == first.OwnerId);
    }

    [Fact]
    public async Task ConcurrentSessions_AreIsolatedBySubjectAndOwner()
    {
        var provider = new SimulatedWfpProvider();
        var engine = Engine(provider);
        var requestOne = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\one.exe"));
        var requestTwo = Request(SubjectTwo, provider.ProviderKind,
            Requested(SubjectTwo, "C:\\BPE-Synthetic\\two.exe"));

        var results = await Task.WhenAll(engine.ApplyAsync(requestOne), engine.ApplyAsync(requestTwo));
        Assert.All(results, x => Assert.Equal("Applied", x.Outcome.ToString()));

        var restoreOne = await engine.RestoreAsync(requestOne.SessionId, provider.ProviderKind);
        Assert.Equal("Restored", restoreOne.Outcome.ToString());

        var afterFirstRestore = (await provider.SnapshotAsync()).Rules;
        Assert.DoesNotContain(afterFirstRestore, x => x.RuleId == requestOne.Rules[0].RuleId);
        Assert.Contains(afterFirstRestore, x => x.RuleId == requestTwo.Rules[0].RuleId);
        Assert.Equal(SubjectTwo, Assert.Single(afterFirstRestore).SubjectSid);

        Assert.Equal("Restored", (await engine.RestoreAsync(requestTwo.SessionId, provider.ProviderKind)).Outcome.ToString());
        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task ExternalModification_IsPreservedAndRestorationReportsConflict()
    {
        var provider = new SimulatedAppLockerProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\target.exe"));
        var applied = await engine.ApplyAsync(request);

        provider.ModifyRuleExternally(request.Rules[0].RuleId);
        var restore = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);

        Assert.Equal("Conflict", restore.Outcome.ToString());
        var changed = Assert.Single((await provider.SnapshotAsync()).Rules);
        Assert.Equal(request.Rules[0].RuleId, changed.RuleId);
        Assert.NotEqual(applied.OwnerId, changed.OwnerId);
    }

    [Fact]
    public async Task RestoreConflict_IsAllOrNothing_AndCanBeRetriedAfterExternalRuleRemoval()
    {
        var provider = new SimulatedAppLockerProvider();
        var journal = new ProtectionJournal();
        var engine = Engine(provider, journal);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\target-one.exe"),
            Requested(SubjectOne, "C:\\BPE-Synthetic\\target-two.exe"));
        await engine.ApplyAsync(request);

        provider.ModifyRuleExternally(request.Rules[1].RuleId);
        var conflict = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);
        Assert.Equal("Conflict", conflict.Outcome.ToString());
        Assert.Equal(2, (await provider.SnapshotAsync()).Rules.Count);

        // An administrator removes the changed rule; explicit retry can then safely
        // remove the remaining rule still owned by this request.
        provider.RemoveRuleExternally(request.Rules[1].RuleId);
        var retry = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);
        Assert.Equal("Restored", retry.Outcome.ToString());
        Assert.Empty((await provider.SnapshotAsync()).Rules);
        Assert.Equal(ProtectionState.Restored, journal.Find(request.RequestId)!.State);
    }

    [Fact]
    public async Task SeparateEngineInstances_SharingJournalSerializeConcurrentSessions()
    {
        var provider = new SimulatedWfpProvider();
        var journal = new ProtectionJournal();
        var engineOne = Engine(provider, journal);
        var engineTwo = Engine(provider, journal);
        var first = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\multi-engine-one.exe"));
        var second = Request(SubjectTwo, provider.ProviderKind,
            Requested(SubjectTwo, "C:\\BPE-Synthetic\\multi-engine-two.exe"));

        var results = await Task.WhenAll(engineOne.ApplyAsync(first), engineTwo.ApplyAsync(second));

        Assert.All(results, result => Assert.Equal("Applied", result.Outcome.ToString()));
        Assert.Equal(2, (await provider.SnapshotAsync()).Rules.Count);
    }

    [Fact]
    public async Task ExistingRuleIdOwnedByAnotherAuthority_IsRejectedAndPreserved()
    {
        var provider = new SimulatedAppLockerProvider();
        var engine = Engine(provider);
        var external = ExternalRule(SubjectTwo, "C:\\BPE-Synthetic\\admin-owned.exe");
        provider.AddExternalRule(external);
        var request = Request(SubjectOne, provider.ProviderKind,
            new RequestedRule(external.RuleId, SubjectOne,
                new ApplicationIdentity("C:\\BPE-Synthetic\\requested.exe", SyntheticHash)));

        var result = await engine.ApplyAsync(request);

        Assert.Equal("Conflict", result.Outcome.ToString());
        var stillExternal = Assert.Single((await provider.SnapshotAsync()).Rules);
        Assert.Equal(external.OwnerId, stillExternal.OwnerId);
        Assert.Equal(SubjectTwo, stillExternal.SubjectSid);
    }

    [Fact]
    public async Task RepeatedCleanup_IsIdempotent()
    {
        var provider = new SimulatedWfpProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\target.exe"));
        await engine.ApplyAsync(request);

        var first = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);
        var second = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);

        Assert.Equal("Restored", first.Outcome.ToString());
        Assert.Equal("NoOp", second.Outcome.ToString());
        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task RestoreFailure_IsNotReportedAsRestored()
    {
        var provider = new SimulatedAppLockerProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectOne, "C:\\BPE-Synthetic\\target.exe"));
        await engine.ApplyAsync(request);
        provider.FailRestore(true);

        var result = await engine.RestoreAsync(request.SessionId, provider.ProviderKind);

        Assert.NotEqual("Restored", result.Outcome.ToString());
        Assert.NotEqual("NoOp", result.Outcome.ToString());
        Assert.Contains((await provider.SnapshotAsync()).Rules, x => x.RuleId == request.Rules[0].RuleId);
    }

    [Fact]
    public async Task RequestAndRuleSidMismatch_IsRejectedBeforeProviderMutation()
    {
        var provider = new SimulatedAppLockerProvider();
        var engine = Engine(provider);
        var request = Request(SubjectOne, provider.ProviderKind,
            Requested(SubjectTwo, "C:\\BPE-Synthetic\\target.exe"));

        var result = await engine.ApplyAsync(request);

        Assert.Equal("InvalidRequest", result.FailureCode.ToString());
        Assert.NotEqual("Applied", result.Outcome.ToString());
        Assert.Empty((await provider.SnapshotAsync()).Rules);
    }

    [Fact]
    public async Task AuditEvents_DoNotContainSyntheticPathSidOrHash()
    {
        var provider = new SimulatedWfpProvider();
        var audit = new InMemoryProtectionAuditSink();
        var engine = new ProtectionEngine([provider], new ProtectionJournal(), audit);
        var identity = new ApplicationIdentity("C:\\BPE-Synthetic\\private-target.exe", SyntheticHash);
        var request = new ProtectionRequest(
            Guid.NewGuid(), Guid.NewGuid(), 1, SubjectOne, provider.ProviderKind,
            [new RequestedRule(Guid.NewGuid(), SubjectOne, identity)]);

        await engine.ApplyAsync(request);
        await engine.RestoreAsync(request.SessionId, provider.ProviderKind);

        var events = JsonSerializer.Serialize(audit.Entries);
        Assert.DoesNotContain("private-target.exe", events, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SubjectOne, events, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticHash, events, StringComparison.OrdinalIgnoreCase);
    }

    private static ProtectionEngine Engine(IProtectionProvider provider, ProtectionJournal? journal = null) =>
        new([provider], journal ?? new ProtectionJournal(), new InMemoryProtectionAuditSink());

    private static ProtectionRequest Request(string sid, ProtectionProviderKind kind, params RequestedRule[] rules) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, sid, kind, rules);

    private static RequestedRule Requested(string sid, string path) =>
        new(Guid.NewGuid(), sid, new ApplicationIdentity(path, SyntheticHash));

    private static ProtectionRule ExternalRule(string sid, string path) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sid, new ApplicationIdentity(path, SyntheticHash));
}
