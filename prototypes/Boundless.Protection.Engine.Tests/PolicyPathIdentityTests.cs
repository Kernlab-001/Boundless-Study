using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class PolicyPathIdentityTests
{
    private const string Subject = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("C:\\BPE-Synthetic\\APP~1\\reader.exe")]
    [InlineData("C:\\BPE-Synthetic\\reader.exe:stream")]
    [InlineData("C:\\BPE-Synthetic\\reader.exe.")]
    [InlineData("\\\\server\\share\\reader.exe")]
    [InlineData("\\\\?\\C:\\BPE-Synthetic\\reader.exe")]
    public void AmbiguousApplicationTargetFailsClosed(string path)
    {
        var target = PolicyTarget.Application(path, Hash);
        var request = Request(target) with
        {
            Process = new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, Subject, path, Hash)
        };

        var result = Evaluator().Evaluate(request, [Rule(target, request.Session)]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.InvalidRequest, result.Status);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public void LexicallyValidApplicationDecisionRemainsSimulationOnly()
    {
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", Hash);
        var request = Request(target);
        var result = Evaluator().Evaluate(request, [Rule(target, request.Session)]);

        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.True(result.IsSimulation);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public void ProtectedCatalogRejectsKnownAmbiguousExecutablePaths()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedTargetCatalog(["C:\\BPE-Synthetic\\APP~1\\reader.exe"]));
    }

    private static ProtectionPolicyEvaluator Evaluator() => new(new ProtectedTargetCatalog());

    private static PolicyEvaluationRequest Request(PolicyTarget target) => new(
        new ProtectionSessionContext(Guid.NewGuid(), 1, Subject, Now.AddMinutes(-1), Now.AddMinutes(1), true),
        new ProcessIdentitySnapshot(42, Now.AddSeconds(-2), Now, Subject, "C:\\BPE-Synthetic\\reader.exe", Hash),
        target,
        Now);

    private static EvaluationRule Rule(PolicyTarget target, ProtectionSessionContext session) => new(
        Guid.NewGuid(), Subject, target, PolicyRuleEffect.Allow,
        SessionId: session.SessionId, SessionGeneration: session.Generation);
}
