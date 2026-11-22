using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// In-memory recovery client for testing and UI design-time preview.
/// </summary>
public sealed class InMemoryRecoveryClient : IRecoveryClient
{
    private RecoveryState _state;
    private bool _shouldFail;
    private string? _failureError;

    public InMemoryRecoveryClient(RecoveryState? initialState = null)
    {
        _state = initialState ?? new RecoveryState(Guid.NewGuid(), null);
    }

    public RecoveryState CurrentState => _state;

    public void SetPendingSession(RecoveryPendingSession? pending)
    {
        _state = _state with { Pending = pending };
    }

    public void ConfigureFailure(string? errorCode = RecoveryErrors.AgentUnavailable, string? errorMessage = "Agent unavailable.")
    {
        _shouldFail = true;
        _failureError = errorCode;
    }

    public void ClearFailure()
    {
        _shouldFail = false;
        _failureError = null;
    }

    public Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (_shouldFail)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                _failureError ?? RecoveryErrors.AgentUnavailable,
                "Agent is currently unavailable.",
                null));
        }

        return Task.FromResult(new RecoveryResponse(
            Guid.NewGuid().ToString("D"),
            true,
            null,
            null,
            _state));
    }

    public Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_shouldFail)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                _failureError ?? RecoveryErrors.AgentUnavailable,
                "Agent is currently unavailable.",
                null));
        }

        if (_state.Pending is null || _state.Pending.SessionId != sessionId)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                RecoveryErrors.SessionNotFound,
                "Pending session was not found.",
                _state));
        }

        if (_state.Pending.IsExpired)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                RecoveryErrors.SessionNotPending,
                "Session has expired and cannot be resumed.",
                _state));
        }

        _state = _state with { Pending = null };
        return Task.FromResult(new RecoveryResponse(
            Guid.NewGuid().ToString("D"),
            true,
            null,
            null,
            _state));
    }

    public Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_shouldFail)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                _failureError ?? RecoveryErrors.AgentUnavailable,
                "Agent is currently unavailable.",
                null));
        }

        if (_state.Pending is null || _state.Pending.SessionId != sessionId)
        {
            return Task.FromResult(new RecoveryResponse(
                Guid.NewGuid().ToString("D"),
                false,
                RecoveryErrors.SessionNotFound,
                "Pending session was not found.",
                _state));
        }

        _state = _state with { Pending = null };
        return Task.FromResult(new RecoveryResponse(
            Guid.NewGuid().ToString("D"),
            true,
            null,
            null,
            _state));
    }
}
