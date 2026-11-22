using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class IdentityTrustBoundaryTests
{
    [Fact]
    public void CallerClaimsAloneCannotBecomeVerifiedEvidence()
    {
        var sessionClaims = new CallerSessionIdentityClaims(Guid.NewGuid(), 1, "S-1-5-21-1-2-3-1001");
        var processClaims = new CallerProcessIdentityClaims(42, DateTimeOffset.UtcNow,
            "S-1-5-21-1-2-3-1001", "C:\\BPE-Synthetic\\reader.exe", new string('a', 64));

        Assert.Equal("S-1-5-21-1-2-3-1001", sessionClaims.SubjectSid);
        Assert.Equal(42, processClaims.ProcessId);
        Assert.False(IdentityTrustBoundary.IsTrustedSession(new SimulatedVerifier(),
            IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("synthetic-claims")));
    }

    [Fact]
    public void SimulatedProcessEvidenceCannotPassTrustedGate()
    {
        var session = IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("no-os-session");
        var process = IdentityVerificationResult<VerifiedProcessIdentity>.Simulated("no-os-process");

        Assert.False(session.IsTrusted);
        Assert.False(process.IsTrusted);
        Assert.False(IdentityTrustBoundary.IsTrustedSession(new SimulatedVerifier(), session));
        Assert.False(IdentityTrustBoundary.IsTrustedProcess(new SimulatedVerifier(), process, null));
    }

    [Fact]
    public void CallerFedEvaluatorRemainsSeparateAndCannotClaimProtection()
    {
        var subject = "S-1-5-21-1-2-3-1001";
        var now = DateTimeOffset.UtcNow;
        var session = new ProtectionSessionContext(Guid.NewGuid(), 1, subject,
            now.AddMinutes(-1), now.AddMinutes(1), true);
        var hash = new string('a', 64);
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", hash);
        var request = new PolicyEvaluationRequest(session,
            new ProcessIdentitySnapshot(42, now.AddSeconds(-2), now, subject,
                "C:\\BPE-Synthetic\\reader.exe", hash), target, now);
        var rule = new EvaluationRule(Guid.NewGuid(), subject, target, PolicyRuleEffect.Allow,
            SessionId: session.SessionId, SessionGeneration: session.Generation);

        var result = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog()).Evaluate(request, [rule]);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.True(result.IsSimulation);
        Assert.False(result.CanClaimProtection);
    }

    private sealed class SimulatedVerifier : IIdentityBoundaryVerifier
    {
        public IdentityEvidenceSourceTrust SourceTrust => IdentityEvidenceSourceTrust.SimulatedUntrusted;

        public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
            CallerSessionIdentityClaims claims, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("no-native-verifier"));

        public Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
            CallerProcessIdentityClaims claims, VerifiedSessionIdentity session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityVerificationResult<VerifiedProcessIdentity>.Simulated("no-native-verifier"));
    }
}
