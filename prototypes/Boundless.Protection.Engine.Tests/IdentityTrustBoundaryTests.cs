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
        var result = IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("synthetic-claims");
        Assert.False(result.IsTrusted);
        Assert.Equal(IdentityEvidenceSourceTrust.SimulatedUntrusted, result.SourceTrust);
    }

    [Fact]
    public void SimulatedProcessEvidenceCannotPassTrustedGate()
    {
        var session = IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("no-os-session");
        var process = IdentityVerificationResult<VerifiedProcessIdentity>.Simulated("no-os-process");

        Assert.False(session.IsTrusted);
        Assert.False(process.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, session.Status);
        Assert.Equal(IdentityVerificationStatus.Rejected, process.Status);
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

    [Fact]
    public async Task AuthorizedEvaluationWithoutTrustedVerifierReturnsUnavailableAndFailsClosed()
    {
        var subject = "S-1-5-21-1-2-3-1001";
        var now = DateTimeOffset.UtcNow;
        var session = new CallerSessionIdentityClaims(Guid.NewGuid(), 1, subject, "caller-asserted");
        var process = new CallerProcessIdentityClaims(42, now.AddSeconds(-1), subject,
            "C:\\BPE-Synthetic\\reader.exe", new string('a', 64));
        var target = PolicyTarget.Application(process.ExecutablePath, process.ExecutableSha256);
        var permissiveRule = new EvaluationRule(Guid.NewGuid(), subject, target, PolicyRuleEffect.Allow,
            SessionId: session.SessionId, SessionGeneration: session.SessionGeneration);

        var result = await new ProtectionPolicyEvaluator(new ProtectedTargetCatalog()).EvaluateAuthorizedAsync(
            session, process, target, now, [permissiveRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, result.Status);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
        Assert.Equal("trusted-identity-verifier-unavailable", result.ReasonCode);
    }

}
