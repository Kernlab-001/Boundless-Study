using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Direct in-process adapter implementing <see cref="IRecoveryClient"/> by delegating to
/// the application recovery use cases. Models the agent-to-UI contract boundary in-process.
/// </summary>
public sealed class ApplicationRecoveryClientAdapter : IRecoveryClient
{
    private readonly Guid _profileId;
    private readonly FocusRecoveryCoordinator _coordinator;
    private bool _isAgentAvailable = true;

    public ApplicationRecoveryClientAdapter(Guid profileId, FocusRecoveryCoordinator coordinator)
    {
        _profileId = profileId;
        _coordinator = coordinator;
    }

    public void SetAgentAvailable(bool available) => _isAgentAvailable = available;

    public async Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAgentAvailable)
        {
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                RecoveryErrors.AgentUnavailable,
                "The agent process is unavailable.");
        }

        var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
        var state = new RecoveryState(_profileId, MapToContract(pending));
        return RecoveryResponse.Ok(Guid.NewGuid().ToString(), state);
    }

    public async Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_isAgentAvailable)
        {
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                RecoveryErrors.AgentUnavailable,
                "The agent process is unavailable.");
        }

        var outcome = await _coordinator.ResumeAsync(_profileId, sessionId, cancellationToken);
        if (!outcome.Success)
        {
            var errorCode = MapErrorCode(outcome.Error?.Code);
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                errorCode,
                outcome.Error?.Message ?? "Failed to resume session.");
        }

        var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
        var state = new RecoveryState(_profileId, MapToContract(pending));
        return RecoveryResponse.Ok(Guid.NewGuid().ToString(), state);
    }

    public async Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_isAgentAvailable)
        {
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                RecoveryErrors.AgentUnavailable,
                "The agent process is unavailable.");
        }

        var outcome = await _coordinator.DiscardAsync(_profileId, sessionId, FocusEndReason.Unknown, cancellationToken);
        if (!outcome.Success)
        {
            var errorCode = MapErrorCode(outcome.Error?.Code);
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                errorCode,
                outcome.Error?.Message ?? "Failed to discard session.");
        }

        var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
        var state = new RecoveryState(_profileId, MapToContract(pending));
        return RecoveryResponse.Ok(Guid.NewGuid().ToString(), state);
    }

    private static RecoveryPendingSession? MapToContract(PendingFocusSessionInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        return new RecoveryPendingSession(
            info.SessionId,
            info.ProfileCode,
            info.TaskTitleSnapshot,
            info.StartedAtUtc,
            info.InterruptedAtUtc,
            info.ExpiresAtUtc,
            (long)info.Age.TotalSeconds,
            info.IsExpired,
            info.PlannedFocusSeconds,
            info.ElapsedSeconds,
            info.ActiveStudySeconds,
            info.EffectiveInterruptionAnchorUtc,
            (long)info.RemainingTime.TotalSeconds);
    }

    private static string MapErrorCode(string? code) => code switch
    {
        UseCaseErrorCodes.ProfileNotFound => RecoveryErrors.ProfileNotFound,
        UseCaseErrorCodes.SessionNotFound => RecoveryErrors.SessionNotFound,
        "SESSION_NOT_PENDING" => RecoveryErrors.SessionNotPending,
        _ => code ?? RecoveryErrors.InvalidRequest,
    };
}

/// <summary>
/// Framed IPC client adapter that simulates named-pipe length-prefixed protocol communication
/// through in-memory streams, testing wire codecs, request validation, and dispatch.
/// </summary>
public sealed class FramedStreamRecoveryClientAdapter : IRecoveryClient
{
    private readonly Guid _profileId;
    private readonly FocusRecoveryCoordinator _coordinator;
    private bool _isPipeBroken;

    public FramedStreamRecoveryClientAdapter(Guid profileId, FocusRecoveryCoordinator coordinator)
    {
        _profileId = profileId;
        _coordinator = coordinator;
    }

    public void BreakPipe() => _isPipeBroken = true;

    public Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default) =>
        ExecuteFramedOperationAsync(RecoveryProtocol.GetStateOperation, null, cancellationToken);

    public Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ExecuteFramedOperationAsync(RecoveryProtocol.ResumeOperation, sessionId, cancellationToken);

    public Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        ExecuteFramedOperationAsync(RecoveryProtocol.DiscardOperation, sessionId, cancellationToken);

    private async Task<RecoveryResponse> ExecuteFramedOperationAsync(
        string operation,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        if (_isPipeBroken)
        {
            return RecoveryResponse.Fail(
                Guid.NewGuid().ToString(),
                RecoveryErrors.AgentUnavailable,
                "Pipe transport failure.");
        }

        var requestId = Guid.NewGuid().ToString();
        var request = new RecoveryRequest(RecoveryProtocol.ProtocolVersion, requestId, operation, sessionId);

        // 1. Client writes framed request
        using var stream = new MemoryStream();
        await RecoveryFrameCodec.WriteAsync(stream, request, cancellationToken);

        // Reset stream position to simulate pipe reading by agent
        stream.Position = 0;

        // 2. Agent reads and validates request frame
        var readResult = await RecoveryFrameCodec.ReadRequestFrameAsync(stream, cancellationToken);
        if (readResult is null || readResult.IsMalformed)
        {
            return RecoveryResponse.Fail(
                readResult?.RequestId ?? requestId,
                RecoveryErrors.ProtocolFailure,
                readResult?.FailureMessage ?? "Malformed request frame.");
        }

        if (!RecoveryRequestValidator.TryValidate(readResult.Request, out var validationError))
        {
            return RecoveryResponse.Fail(requestId, RecoveryErrors.InvalidRequest, validationError);
        }

        // 3. Agent dispatches to application use cases
        RecoveryResponse response;
        switch (operation)
        {
            case RecoveryProtocol.GetStateOperation:
            {
                var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
                var state = new RecoveryState(_profileId, MapToContract(pending));
                response = RecoveryResponse.Ok(requestId, state);
                break;
            }
            case RecoveryProtocol.ResumeOperation:
            {
                var outcome = await _coordinator.ResumeAsync(_profileId, sessionId!.Value, cancellationToken);
                if (!outcome.Success)
                {
                    response = RecoveryResponse.Fail(requestId, MapErrorCode(outcome.Error?.Code), outcome.Error?.Message ?? "Resume failed.");
                }
                else
                {
                    var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
                    response = RecoveryResponse.Ok(requestId, new RecoveryState(_profileId, MapToContract(pending)));
                }
                break;
            }
            case RecoveryProtocol.DiscardOperation:
            {
                var outcome = await _coordinator.DiscardAsync(_profileId, sessionId!.Value, FocusEndReason.Unknown, cancellationToken);
                if (!outcome.Success)
                {
                    response = RecoveryResponse.Fail(requestId, MapErrorCode(outcome.Error?.Code), outcome.Error?.Message ?? "Discard failed.");
                }
                else
                {
                    var pending = await _coordinator.GetPendingAsync(_profileId, cancellationToken);
                    response = RecoveryResponse.Ok(requestId, new RecoveryState(_profileId, MapToContract(pending)));
                }
                break;
            }
            default:
                response = RecoveryResponse.Fail(requestId, RecoveryErrors.InvalidRequest, "Unknown operation.");
                break;
        }

        // 4. Agent writes framed response
        using var responseStream = new MemoryStream();
        await RecoveryFrameCodec.WriteAsync(responseStream, response, cancellationToken);

        // 5. Client reads response frame
        responseStream.Position = 0;
        var responseFromWire = await RecoveryFrameCodec.ReadResponseAsync(responseStream, cancellationToken);
        return responseFromWire ?? RecoveryResponse.Fail(requestId, RecoveryErrors.ProtocolFailure, "Empty response from agent.");
    }

    private static RecoveryPendingSession? MapToContract(PendingFocusSessionInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        return new RecoveryPendingSession(
            info.SessionId,
            info.ProfileCode,
            info.TaskTitleSnapshot,
            info.StartedAtUtc,
            info.InterruptedAtUtc,
            info.ExpiresAtUtc,
            (long)info.Age.TotalSeconds,
            info.IsExpired,
            info.PlannedFocusSeconds,
            info.ElapsedSeconds,
            info.ActiveStudySeconds,
            info.EffectiveInterruptionAnchorUtc,
            (long)info.RemainingTime.TotalSeconds);
    }

    private static string MapErrorCode(string? code) => code switch
    {
        UseCaseErrorCodes.ProfileNotFound => RecoveryErrors.ProfileNotFound,
        UseCaseErrorCodes.SessionNotFound => RecoveryErrors.SessionNotFound,
        "SESSION_NOT_PENDING" => RecoveryErrors.SessionNotPending,
        _ => code ?? RecoveryErrors.InvalidRequest,
    };
}
