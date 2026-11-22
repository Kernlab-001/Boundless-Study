using Boundless.Protection.Engine;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class SimulationClaimTests
{
    [Fact]
    public void DecisionMutationAndCapabilityResultsCannotClaimRealProtection()
    {
        var result = new ProtectionResult(ProtectionOutcome.Applied, ProtectionState.Active,
            ProtectionFailureCode.None, 1, 0);
        var mutation = new ProviderMutationResult(ProtectionOutcome.Applied,
            ProtectionFailureCode.None, Array.Empty<ProtectionRule>());
        var decision = new PolicyEvaluationResult(PolicyDecision.Restrict,
            PolicyEvaluationStatus.RestrictedByRule, true);
        var available = new ProviderCapabilityStatus(ProtectionProviderKind.AppLocker,
            ProviderCapabilityState.Available, CanInspect: true, CanApply: true, CanRestore: true);

        Assert.True(result.IsSimulation);
        Assert.False(result.CanClaimProtection);
        Assert.True(mutation.IsSimulation);
        Assert.False(mutation.CanClaimProtection);
        Assert.True(decision.IsSimulation);
        Assert.False(decision.CanClaimProtection);
        Assert.False(available.CanClaimProtection);
    }
}
