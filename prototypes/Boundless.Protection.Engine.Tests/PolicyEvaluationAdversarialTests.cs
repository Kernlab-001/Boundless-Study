using Boundless.Protection.Engine;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class PolicyEvaluationAdversarialTests
{
    private const string Subject = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string OtherSubject = "S-1-5-21-111111111-222222222-333333333-1002";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid SessionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExceptionOverridesDeny_AndDenyOverridesAllow_RegardlessOfPriority()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var evaluator = Evaluator();
        var rules = new[]
        {
            Rule(target, PolicyRuleEffect.Allow, priority: 100),
            Rule(target, PolicyRuleEffect.Deny, priority: -100),
            Rule(target, PolicyRuleEffect.Exception, priority: -1000)
        };

        var result = evaluator.Evaluate(Request(target), rules);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.AllowedByException, result.Status);
        Assert.Equal(rules[2].RuleId, result.MatchedRuleId);
    }

    [Fact]
    public void DenyOverridesAllow_WhenNoExceptionMatches()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var rules = new[] { Rule(target, PolicyRuleEffect.Allow, 100), Rule(target, PolicyRuleEffect.Deny, -100) };

        var result = Evaluator().Evaluate(Request(target), rules);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByRule, result.Status);
        Assert.Equal(rules[1].RuleId, result.MatchedRuleId);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public void NoMatchingRule_DefaultsToRestrict()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\unlisted.exe", Hash);

        var result = Evaluator().Evaluate(Request(target), Array.Empty<EvaluationRule>());

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public void ApplicationTargetMustMatchTheSuppliedProcessIdentity()
    {
        var evaluatedTarget = PolicyTarget.Application("C:\\BPE-Synthetic\\different.exe", Hash);
        var rule = Rule(evaluatedTarget, PolicyRuleEffect.Allow);

        var mismatchedRequest = Request(evaluatedTarget) with
        {
            Process = new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, Subject,
                "C:\\BPE-Synthetic\\reader.exe", Hash)
        };
        var result = Evaluator().Evaluate(mismatchedRequest, [rule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityMismatch, result.Status);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public void MalformedEnabledRuleFailsClosed_InsteadOfBeingIgnored()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var malformedDeny = new EvaluationRule(Guid.Empty, Subject, target, PolicyRuleEffect.Deny);

        var result = Evaluator().Evaluate(Request(target), [malformedDeny]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.InvalidRequest, result.Status);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public void InactiveExpiredOrWrongUserSessionFailsClosed()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var request = Request(target) with
        {
            Session = new ProtectionSessionContext(Guid.NewGuid(), 1, Subject,
                Now.AddMinutes(-10), Now.AddMinutes(-1), IsActive: true)
        };

        var result = Evaluator().Evaluate(request, [Rule(target, PolicyRuleEffect.Allow)]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.InactiveSession, result.Status);
    }

    [Fact]
    public void UserSidMismatchFailsClosed()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var request = Request(target) with
        {
            Process = new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, OtherSubject,
                "C:\\BPE-Synthetic\\reader.exe", Hash)
        };

        var result = Evaluator().Evaluate(request, [Rule(target, PolicyRuleEffect.Allow)]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityMismatch, result.Status);
    }

    [Fact]
    public void RuleFromDifferentSessionCannotApply()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var otherSessionRule = Rule(target, PolicyRuleEffect.Allow) with { SessionId = Guid.NewGuid() };

        var result = Evaluator().Evaluate(Request(target), [otherSessionRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByDefault, result.Status);
    }

    [Fact]
    public void StaleProcessIdentityFailsClosed()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var request = Request(target) with
        {
            Process = new ProcessIdentitySnapshot(42, Now.AddMinutes(-2), Now.AddMinutes(-1), Subject,
                "C:\\BPE-Synthetic\\reader.exe", Hash)
        };

        var result = Evaluator().Evaluate(request, [Rule(target, PolicyRuleEffect.Allow)]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.StaleProcessIdentity, result.Status);
    }

    [Fact]
    public void ProtectedAccessibilityTargetIsExcludedBeforeAllowRule()
    {
        var target = PolicyTarget.Application("C:\\Windows\\System32\\Narrator.exe", Hash);
        var evaluator = new ProtectionPolicyEvaluator(ProtectedTargetCatalog.ForWindowsDirectory("C:\\Windows"));

        var result = evaluator.Evaluate(Request(target), [Rule(target, PolicyRuleEffect.Allow)]);

        Assert.Equal(PolicyDecision.ExcludedProtectedTarget, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.ExcludedProtectedTarget, result.Status);
    }

    [Fact]
    public void NetworkRuleMatchesCanonicalHostAndRestrictsDestination()
    {
        var target = PolicyTarget.Network("EXAMPLE.COM.", 443, NetworkProtocol.Tcp);
        var rule = Rule(PolicyTarget.Network("example.com", 443, NetworkProtocol.Tcp), PolicyRuleEffect.Deny);

        var result = Evaluator().Evaluate(Request(target), [rule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByRule, result.Status);
    }

    private static ProtectionPolicyEvaluator Evaluator() => new(new ProtectedTargetCatalog());

    private static PolicyEvaluationRequest Request(PolicyTarget target) => new(
        new ProtectionSessionContext(SessionId, 1, Subject, Now.AddMinutes(-1), Now.AddMinutes(30), true),
        target.Kind == PolicyTargetKind.Application
            ? new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, Subject, target.ExecutablePath!, target.Sha256!)
            : new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, Subject, "C:\\BPE-Synthetic\\reader.exe", Hash),
        target,
        Now);

    private static EvaluationRule Rule(PolicyTarget target, PolicyRuleEffect effect, int priority = 0, string? subject = null) =>
        new(Guid.NewGuid(), subject ?? Subject, target, effect, priority,
            SessionId: SessionId, SessionGeneration: 1);
}

public sealed class ProviderCapabilityAdversarialTests
{
    private const string TestSubject = "S-1-5-21-111111111-222222222-333333333-1001";
    [Fact]
    public async Task SimulatedProviderNeverClaimsOperatingSystemProtection()
    {
        var simulator = new SimulatedAppLockerProvider();
        var adapter = new SimulatedApplicationEnforcementProvider(simulator);

        var status = await adapter.DiscoverCapabilityAsync();

        Assert.Equal(ProviderCapabilityState.Simulated, status.State);
        Assert.True(status.IsSimulation);
        Assert.False(status.CanClaimProtection);
    }

    [Fact]
    public async Task MissingAndUnavailableProvidersAreDistinguishedFromSimulation()
    {
        var missing = await new SimulatedProtectionCapabilityDetector([]).DetectAsync(ProtectionProviderKind.AppLocker);
        Assert.Equal(ProviderCapabilityState.Unavailable, missing.State);
        Assert.False(missing.CanClaimProtection);

        var simulator = new SimulatedAppLockerProvider();
        simulator.SetAvailable(false);
        var detector = new SimulatedProtectionCapabilityDetector(
            [new SimulatedApplicationEnforcementProvider(simulator)]);
        var unavailable = await detector.DetectAsync(ProtectionProviderKind.AppLocker);
        Assert.Equal(ProviderCapabilityState.Unavailable, unavailable.State);
        Assert.False(unavailable.CanClaimProtection);
    }

    [Fact]
    public async Task CapabilityProbeExceptionsBecomeTypedUnsupportedOrErrorStates()
    {
        var unsupported = new SimulatedProtectionCapabilityDetector([new ThrowingProvider(true)]);
        var unsupportedStatus = await unsupported.DetectAsync(ProtectionProviderKind.AppLocker);
        Assert.Equal(ProviderCapabilityState.Unsupported, unsupportedStatus.State);
        Assert.False(unsupportedStatus.CanClaimProtection);

        var error = new SimulatedProtectionCapabilityDetector([new ThrowingProvider(false)]);
        var errorStatus = await error.DetectAsync(ProtectionProviderKind.AppLocker);
        Assert.Equal(ProviderCapabilityState.Error, errorStatus.State);
        Assert.False(errorStatus.CanClaimProtection);
    }

    [Fact]
    public async Task CancellationDuringRestoreLeavesRecoverableJournalAndLaterRecoveryFinishes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journal = new SqliteProtectionJournal(Path.Combine(directory, "journal.db"));
            var provider = new PersistentSimulatedPolicyProvider(Path.Combine(directory, "policy.db"), ProtectionProviderKind.AppLocker);
            var rule = new RequestedRule(Guid.NewGuid(), TestSubject,
                new ApplicationIdentity("C:\\BPE-Synthetic\\cancel-target.exe", new string('c', 64)));
            var request = new ProtectionRequest(Guid.NewGuid(), Guid.NewGuid(), 1,
                TestSubject, ProtectionProviderKind.AppLocker, [rule]);
            Assert.Equal(ProtectionOutcome.Applied,
                (await new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink()).ApplyAsync(request)).Outcome);

            using var cancellation = new CancellationTokenSource();
            var cancelOnRestore = new CancelOnRestoreProvider(provider, cancellation);
            var engine = new ProtectionEngine([cancelOnRestore], journal, new InMemoryProtectionAuditSink());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.RestoreAsync(request.SessionId, request.ProviderKind, cancellation.Token));

            Assert.Equal(ProtectionState.Restoring, journal.Find(request.RequestId)!.State);
            var reopenedJournal = new SqliteProtectionJournal(Path.Combine(directory, "journal.db"));
            var reopenedProvider = new PersistentSimulatedPolicyProvider(Path.Combine(directory, "policy.db"), ProtectionProviderKind.AppLocker);
            var recovered = await new ProtectionEngine([reopenedProvider], reopenedJournal,
                new InMemoryProtectionAuditSink()).RecoverAsync();
            Assert.Single(recovered);
            Assert.Equal(ProtectionOutcome.Restored, recovered[0].Outcome);
            Assert.Equal(ProtectionState.Restored, reopenedJournal.Find(request.RequestId)!.State);
            Assert.Empty((await reopenedProvider.SnapshotAsync()).Rules);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class ThrowingProvider(bool unsupported) : IApplicationEnforcementProvider
    {
        public ProtectionProviderKind Kind => ProtectionProviderKind.AppLocker;
        public Task<PolicySnapshot> InspectAsync(CancellationToken cancellationToken = default) => throw ProbeException();
        public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default) => throw ProbeException();
        public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactOwnedRules, CancellationToken cancellationToken = default) => throw ProbeException();
        public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw ProbeException();
        public Task<ProviderCapabilityStatus> DiscoverCapabilityAsync(CancellationToken cancellationToken = default) => throw ProbeException();
        private Exception ProbeException() => unsupported ? new NotSupportedException("Synthetic unsupported probe.") : new InvalidOperationException("Synthetic probe error.");
    }

    private sealed class CancelOnRestoreProvider(IProtectionProvider inner, CancellationTokenSource cancellation) : IProtectionProvider
    {
        public ProtectionProviderKind Kind => inner.Kind;
        public Task<PolicySnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => inner.SnapshotAsync(cancellationToken);
        public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default) =>
            inner.ApplyAsync(ownerId, rules, expectedVersion, expectedFingerprint, cancellationToken);
        public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
            inner.GetOwnedRulesAsync(ownerId, cancellationToken);
        public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactRules, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return inner.RestoreAsync(ownerId, exactRules, cancellationToken);
        }
    }
}
