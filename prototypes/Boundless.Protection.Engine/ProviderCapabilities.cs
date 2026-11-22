namespace Boundless.Protection.Engine;

/// <summary>
/// Describes provider readiness without conflating a test simulator with real operating
/// system enforcement. Only <see cref="Available"/> may describe a real provider.
/// </summary>
public enum ProviderCapabilityState
{
    Simulated,
    Available,
    Unavailable,
    Unsupported,
    Error
}

public sealed record ProviderCapabilityStatus(
    ProtectionProviderKind ProviderKind,
    ProviderCapabilityState State,
    bool CanInspect,
    bool CanApply,
    bool CanRestore,
    string? FailureCode = null)
{
    /// <summary>
    /// BPE-1.5 contains no operating-system provider, so no capability result from this
    /// assembly can claim that OS protection is active.
    /// </summary>
    public bool CanClaimProtection => false;

    /// <summary>Simulation may be exercised in tests but can never be represented as active OS protection.</summary>
    public bool IsSimulation => State == ProviderCapabilityState.Simulated;
}

/// <summary>Shared provider contract for policy inspection, application, and ownership-scoped restoration.</summary>
public interface IPolicyEnforcementProvider
{
    ProtectionProviderKind Kind { get; }
    Task<PolicySnapshot> InspectAsync(CancellationToken cancellationToken = default);
    Task<ProviderMutationResult> ApplyAsync(
        Guid ownerId,
        IReadOnlyList<RequestedRule> rules,
        long expectedVersion,
        string expectedFingerprint,
        CancellationToken cancellationToken = default);
    Task<ProviderMutationResult> RestoreAsync(
        Guid ownerId,
        IReadOnlyList<ProtectionRule> exactOwnedRules,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default);
}

/// <summary>Application enforcement provider contract. Current implementations are explicitly simulated.</summary>
public interface IApplicationEnforcementProvider : IPolicyEnforcementProvider
{
    Task<ProviderCapabilityStatus> DiscoverCapabilityAsync(CancellationToken cancellationToken = default);
}

/// <summary>Network enforcement provider contract. Current implementations are explicitly simulated.</summary>
public interface INetworkEnforcementProvider : IPolicyEnforcementProvider
{
    Task<ProviderCapabilityStatus> DiscoverCapabilityAsync(CancellationToken cancellationToken = default);
}

public interface IProtectionCapabilityDetector
{
    Task<ProviderCapabilityStatus> DetectAsync(ProtectionProviderKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Marks a provider adapter that is guaranteed not to claim real enforcement.</summary>
public abstract class SimulatedPolicyProviderAdapter : IPolicyEnforcementProvider
{
    private readonly IProtectionProvider _inner;

    protected SimulatedPolicyProviderAdapter(IProtectionProvider inner, ProtectionProviderKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner.Kind != expectedKind)
            throw new ArgumentException($"A {expectedKind} simulator is required.", nameof(inner));
        _inner = inner;
        Kind = expectedKind;
    }

    public ProtectionProviderKind Kind { get; }
    public Task<PolicySnapshot> InspectAsync(CancellationToken cancellationToken = default) => _inner.SnapshotAsync(cancellationToken);
    public Task<ProviderMutationResult> ApplyAsync(Guid ownerId, IReadOnlyList<RequestedRule> rules, long expectedVersion, string expectedFingerprint, CancellationToken cancellationToken = default) =>
        _inner.ApplyAsync(ownerId, rules, expectedVersion, expectedFingerprint, cancellationToken);
    public Task<ProviderMutationResult> RestoreAsync(Guid ownerId, IReadOnlyList<ProtectionRule> exactOwnedRules, CancellationToken cancellationToken = default) =>
        _inner.RestoreAsync(ownerId, exactOwnedRules, cancellationToken);
    public Task<IReadOnlyList<ProtectionRule>> GetOwnedRulesAsync(Guid ownerId, CancellationToken cancellationToken = default) =>
        _inner.GetOwnedRulesAsync(ownerId, cancellationToken);

    protected async Task<ProviderCapabilityStatus> DiscoverSimulationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await InspectAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderCapabilityStatus(Kind, ProviderCapabilityState.Simulated,
                CanInspect: true, CanApply: true, CanRestore: true);
        }
        catch (ProtectionProviderUnavailableException)
        {
            return new ProviderCapabilityStatus(Kind, ProviderCapabilityState.Unavailable,
                CanInspect: false, CanApply: false, CanRestore: false, nameof(ProtectionFailureCode.ProviderUnavailable));
        }
        catch (NotSupportedException)
        {
            return new ProviderCapabilityStatus(Kind, ProviderCapabilityState.Unsupported,
                CanInspect: false, CanApply: false, CanRestore: false, "Unsupported");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProviderCapabilityStatus(Kind, ProviderCapabilityState.Error,
                CanInspect: false, CanApply: false, CanRestore: false, "CapabilityProbeFailed");
        }
    }
}

public sealed class SimulatedApplicationEnforcementProvider(IProtectionProvider inner)
    : SimulatedPolicyProviderAdapter(inner, ProtectionProviderKind.AppLocker), IApplicationEnforcementProvider
{
    public Task<ProviderCapabilityStatus> DiscoverCapabilityAsync(CancellationToken cancellationToken = default) =>
        DiscoverSimulationAsync(cancellationToken);
}

public sealed class SimulatedNetworkEnforcementProvider(IProtectionProvider inner)
    : SimulatedPolicyProviderAdapter(inner, ProtectionProviderKind.Wfp), INetworkEnforcementProvider
{
    public Task<ProviderCapabilityStatus> DiscoverCapabilityAsync(CancellationToken cancellationToken = default) =>
        DiscoverSimulationAsync(cancellationToken);
}

/// <summary>
/// Capability detector for the current platform-independent simulator. Missing providers
/// are unavailable; only explicit adapters can report simulated capability.
/// </summary>
public sealed class SimulatedProtectionCapabilityDetector : IProtectionCapabilityDetector
{
    private readonly IReadOnlyDictionary<ProtectionProviderKind, IPolicyEnforcementProvider> _providers;

    public SimulatedProtectionCapabilityDetector(IEnumerable<IPolicyEnforcementProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var values = providers.ToArray();
        if (values.Any(provider => provider is null)) throw new ArgumentException("Providers cannot contain null.", nameof(providers));
        if (values.Select(provider => provider.Kind).Distinct().Count() != values.Length)
            throw new ArgumentException("Only one provider per kind can be registered.", nameof(providers));
        _providers = values.ToDictionary(provider => provider.Kind);
    }

    public async Task<ProviderCapabilityStatus> DetectAsync(ProtectionProviderKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind))
            return new ProviderCapabilityStatus(kind, ProviderCapabilityState.Error,
                CanInspect: false, CanApply: false, CanRestore: false, "InvalidProviderKind");
        if (!_providers.TryGetValue(kind, out var provider))
            return new ProviderCapabilityStatus(kind, ProviderCapabilityState.Unavailable,
                CanInspect: false, CanApply: false, CanRestore: false, nameof(ProtectionFailureCode.ProviderUnavailable));

        try
        {
            return provider switch
            {
                IApplicationEnforcementProvider app => await app.DiscoverCapabilityAsync(cancellationToken).ConfigureAwait(false),
                INetworkEnforcementProvider network => await network.DiscoverCapabilityAsync(cancellationToken).ConfigureAwait(false),
                _ => new ProviderCapabilityStatus(kind, ProviderCapabilityState.Unsupported,
                    CanInspect: false, CanApply: false, CanRestore: false, "NoCapabilityContract")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return new ProviderCapabilityStatus(kind, ProviderCapabilityState.Unsupported,
                CanInspect: false, CanApply: false, CanRestore: false, "Unsupported");
        }
        catch
        {
            return new ProviderCapabilityStatus(kind, ProviderCapabilityState.Error,
                CanInspect: false, CanApply: false, CanRestore: false, "CapabilityProbeFailed");
        }
    }
}
