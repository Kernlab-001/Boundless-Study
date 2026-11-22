using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// The UI's only data boundary (ADR-0005). Implementations use the agent UI pipe and
/// must never open SQLite. Transport failures are returned as a failed
/// <see cref="RecoveryResponse"/> with <see cref="RecoveryErrors.AgentUnavailable"/>
/// so the view model can offer retry/repair instead of stale data.
/// </summary>
public interface IRecoveryClient
{
    Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default);

    Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
