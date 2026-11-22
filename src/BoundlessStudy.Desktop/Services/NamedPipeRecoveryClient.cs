using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Connects to the agent's Named Pipe interface for recovery operations using the unified IPC client (ADR-0005).
/// Delegates to <see cref="IAgentClient"/> (<see cref="StudyAgentClient"/>), which handles
/// request framing, SID authorization, and error sanitization.
/// Desktop never references BoundlessStudy.Infrastructure or opens SQLite directly.
/// </summary>
public sealed class NamedPipeRecoveryClient : IRecoveryClient, IAsyncDisposable, IDisposable
{
    private readonly IAgentClient _agentClient;
    private readonly bool _ownsClient;

    public NamedPipeRecoveryClient(IAgentClient agentClient)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _ownsClient = false;
    }

    public NamedPipeRecoveryClient(string? pipeName = null, int connectTimeoutMs = 3000)
    {
        _agentClient = new StudyAgentClient(pipeName, connectTimeoutMs);
        _ownsClient = true;
    }

    public IAgentClient AgentClient => _agentClient;

    public Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default) =>
        _agentClient.GetStateAsync(cancellationToken);

    public Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _agentClient.ResumeAsync(sessionId, cancellationToken);

    public Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _agentClient.DiscardAsync(sessionId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_ownsClient && _agentClient is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
