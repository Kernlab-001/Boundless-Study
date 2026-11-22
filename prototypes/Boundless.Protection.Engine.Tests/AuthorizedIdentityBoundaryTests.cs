using System.Reflection;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class AuthorizedIdentityBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private const string Subject = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("S-1-5-21-111111111-222222222-333333333-1001", 5, 42)]
    [InlineData("S-1-5-21-111111111-222222222-333333333-9999", 6, 43)]
    [InlineData("forged-sid", -1, -42)]
    public async Task PublicAuthorizationEntryFailsClosedForCallerClaims(
        string subjectSid, long generationClaim, int processIdClaim)
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var session = new CallerSessionIdentityClaims(Guid.NewGuid(), generationClaim, subjectSid, "caller-asserted");
        var process = new CallerProcessIdentityClaims(processIdClaim, Now.AddDays(-1), subjectSid,
            "C:\\BPE-Synthetic\\forged.exe", new string('f', 64));
        var rule = new EvaluationRule(Guid.NewGuid(), subjectSid, target, PolicyRuleEffect.Allow,
            SessionId: session.SessionId, SessionGeneration: generationClaim);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        var result = await evaluator.EvaluateAuthorizedAsync(session, process, target, Now, [rule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, result.Status);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
        Assert.Equal("trusted-identity-verifier-unavailable", result.ReasonCode);
    }

    [Fact]
    public async Task NoTargetOrRuleCanTurnSimulatedClaimsIntoAuthorization()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var session = new CallerSessionIdentityClaims(Guid.NewGuid(), 1, Subject);
        var process = new CallerProcessIdentityClaims(42, Now.AddSeconds(-1), Subject,
            "C:\\BPE-Synthetic\\reader.exe", Hash);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var allow = new EvaluationRule(Guid.NewGuid(), Subject, target, PolicyRuleEffect.Allow,
            SessionId: session.SessionId, SessionGeneration: session.SessionGeneration);

        var withAllow = await evaluator.EvaluateAuthorizedAsync(session, process, target, Now, [allow]);
        var withoutRules = await evaluator.EvaluateAuthorizedAsync(session, process, target, Now, []);

        Assert.Equal(PolicyDecision.Restrict, withAllow.Decision);
        Assert.Equal(PolicyDecision.Restrict, withoutRules.Decision);
        Assert.All(new[] { withAllow, withoutRules }, result =>
        {
            Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, result.Status);
            Assert.True(result.IsSimulation);
            Assert.False(result.CanClaimProtection);
        });
    }

    [Fact]
    public void PublicApiDoesNotAcceptCallerSelectedVerifierOrExposeTrustedIssuer()
    {
        var assembly = typeof(ProtectionPolicyEvaluator).Assembly;
        var verifierType = assembly.GetType("Boundless.Protection.Engine.IIdentityBoundaryVerifier", throwOnError: true)!;
        Assert.False(verifierType.IsPublic);

        var authorizedMethod = typeof(ProtectionPolicyEvaluator).GetMethod(nameof(ProtectionPolicyEvaluator.EvaluateAuthorizedAsync),
            BindingFlags.Public | BindingFlags.Instance)!;
        Assert.DoesNotContain(authorizedMethod.GetParameters(), parameter => parameter.ParameterType == verifierType);

        var trustedFactories = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(method => method.Name is "Verified" or "CreateVerified" or "IssueVerified")
            .ToArray();
        Assert.Empty(trustedFactories);
    }

    [Fact]
    public async Task CancellationRemainsObservableAtAuthorizationBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluator.EvaluateAuthorizedAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, Subject),
            new CallerProcessIdentityClaims(42, Now, Subject, "C:\\BPE-Synthetic\\reader.exe", Hash),
            PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash), Now, [], cancellation.Token));
    }
}
