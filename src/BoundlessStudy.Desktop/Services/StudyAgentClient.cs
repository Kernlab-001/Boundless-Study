using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Production Desktop client connecting to the agent's Named Pipe interface (system-architecture.md §7.2; ADR-0005).
/// Connects to BoundlessStudy.UI.&lt;UserSid&gt; over Named Pipes with timeout and auto-reconnect capability.
/// Desktop never touches SQLite or Infrastructure directly.
/// </summary>
public sealed class StudyAgentClient : IAgentClient
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public StudyAgentClient(string? pipeName = null, int connectTimeoutMs = 3000)
    {
        _connectTimeoutMs = connectTimeoutMs;
        _pipeName = pipeName ?? ResolveDefaultPipeName();
    }

    public string PipeName => _pipeName;

    public int ConnectTimeoutMs => _connectTimeoutMs;

    private static string ResolveDefaultPipeName()
    {
        try
        {
            var userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(userSid))
            {
                return IpcProtocol.PipeNameForUser(userSid);
            }
        }
        catch
        {
            // Fallback if WindowsIdentity is unavailable
        }

        return IpcProtocol.PipeNamePrefix + "Default";
    }

    public async Task<IpcResponse> SendRawRequestAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendRequestInternalAsync(request, cancellationToken);
    }

    private async Task<IpcResponse> SendRequestInternalAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int maxAttempts = 2; // Initial attempt + 1 reconnect retry on broken pipe
        int attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await EnsureConnectedAsync(cancellationToken);
                await IpcFrameCodec.WriteRequestAsync(_pipe!, request, cancellationToken);
                var response = await IpcFrameCodec.ReadResponseAsync(_pipe!, cancellationToken);

                if (response is null)
                {
                    DisposePipe();
                    if (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    return IpcResponse.Fail(
                        request.RequestId,
                        request.CorrelationId,
                        IpcErrors.ProtocolFailure,
                        "The agent closed the connection without returning a response.");
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DisposePipe();
                return IpcResponse.Fail(
                    request.RequestId,
                    request.CorrelationId,
                    IpcErrors.AgentUnavailable,
                    "The background agent did not respond within the connection timeout.");
            }
            catch (Exception ex) when (ex is System.IO.IOException or TimeoutException)
            {
                DisposePipe();
                if (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                return IpcResponse.Fail(
                    request.RequestId,
                    request.CorrelationId,
                    IpcErrors.AgentUnavailable,
                    "Could not connect to the background agent service.");
            }
            catch (Exception ex)
            {
                DisposePipe();
                return IpcResponse.Fail(
                    request.RequestId,
                    request.CorrelationId,
                    IpcErrors.AgentUnavailable,
                    $"IPC communication with the agent failed: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        return IpcResponse.Fail(
            request.RequestId,
            request.CorrelationId,
            IpcErrors.AgentUnavailable,
            "Could not connect to the background agent service.");
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        DisposePipe();

        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeoutCts = new CancellationTokenSource(_connectTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await pipe.ConnectAsync(linkedCts.Token);
            _pipe = pipe;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException($"Connection to agent pipe '{_pipeName}' timed out after {_connectTimeoutMs}ms.");
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private void DisposePipe()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch { }
        finally
        {
            _pipe = null;
        }
    }

    // -------------------------------------------------------------------------------------
    // IAgentClient Implementation
    // -------------------------------------------------------------------------------------

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.PingOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response.Success;
    }

    public async Task<ProfileProgressDto?> GetProfileProgressAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetProfileProgressOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to get profile progress.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return null;
        }

        return JsonSerializer.Deserialize<ProfileProgressDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
    }

    public async Task<ProtectionStatusDto> GetProtectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetProtectionStatusOperation,
            DateTime.UtcNow);
        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to get protection status.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return new ProtectionStatusDto("Unavailable", false, false, 0, 0, 0, null, "The Agent did not return a protection status.");
        }

        return JsonSerializer.Deserialize<ProtectionStatusDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            ?? new ProtectionStatusDto("Unavailable", false, false, 0, 0, 0, null, "The Agent returned an invalid protection status.");
    }

    public async Task<ApplicationInterventionDto?> GetApplicationInterventionAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetApplicationInterventionOperation,
            DateTime.UtcNow);
        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to get application intervention state.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson)) return null;
        return JsonSerializer.Deserialize<ApplicationInterventionDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
    }

    public async Task AcknowledgeApplicationOverlayAsync(ApplicationOverlayAckRequest ack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplicationOverlayAckOperation,
            DateTime.UtcNow,
            JsonSerializer.Serialize(ack, IpcFrameCodec.SerializerOptions));
        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "The Agent did not acknowledge the application overlay.");
        }
    }

    public async Task GrantSessionExceptionAsync(GrantExceptionRequest exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GrantExceptionOperation,
            DateTime.UtcNow,
            JsonSerializer.Serialize(exception, IpcFrameCodec.SerializerOptions));
        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "The temporary exception could not be requested.");
        }
    }

    public async Task<IReadOnlyList<SubjectDto>> GetSubjectsAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(new { includeArchived }, IpcFrameCodec.SerializerOptions);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListSubjectsOperation,
            DateTime.UtcNow,
            payloadJson);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to list subjects.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return Array.Empty<SubjectDto>();
        }

        return JsonSerializer.Deserialize<List<SubjectDto>>(response.PayloadJson, IpcFrameCodec.SerializerOptions) ?? (IReadOnlyList<SubjectDto>)Array.Empty<SubjectDto>();
    }

    public async Task<SubjectDto> CreateSubjectAsync(CreateSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateSubjectOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to create subject.");
        }

        if (!string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            // If response returned full SubjectDto
            try
            {
                var dto = JsonSerializer.Deserialize<SubjectDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
                if (dto is not null && dto.Id != Guid.Empty)
                {
                    return dto;
                }
            }
            catch { }

            // Or if returned { "id": "..." }
            try
            {
                using var doc = JsonDocument.Parse(response.PayloadJson);
                if ((doc.RootElement.TryGetProperty("id", out var p) || doc.RootElement.TryGetProperty("Id", out p)) && p.TryGetGuid(out var id))
                {
                    return new SubjectDto(id, request.Name.Trim(), request.Description?.Trim(), false, DateTime.UtcNow);
                }
            }
            catch { }
        }

        return new SubjectDto(Guid.NewGuid(), request.Name.Trim(), request.Description?.Trim(), false, DateTime.UtcNow);
    }

    public async Task<SubjectDto> UpdateSubjectAsync(UpdateSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.UpdateSubjectOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to update subject.");
        }

        return new SubjectDto(request.Id, request.Name.Trim(), request.Description?.Trim(), false, DateTime.UtcNow);
    }

    public async Task<SubjectDto> ArchiveSubjectAsync(ArchiveSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ArchiveSubjectOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to archive subject.");
        }

        return new SubjectDto(request.Id, string.Empty, null, !request.Restore, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<StudyTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListTasksOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to list tasks.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return Array.Empty<StudyTaskDto>();
        }

        return JsonSerializer.Deserialize<List<StudyTaskDto>>(response.PayloadJson, IpcFrameCodec.SerializerOptions) ?? (IReadOnlyList<StudyTaskDto>)Array.Empty<StudyTaskDto>();
    }

    public async Task<StudyTaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to create task.");
        }

        if (!string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<StudyTaskDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
                if (dto is not null && dto.Id != Guid.Empty)
                {
                    return dto;
                }
            }
            catch { }

            try
            {
                using var doc = JsonDocument.Parse(response.PayloadJson);
                if ((doc.RootElement.TryGetProperty("id", out var p) || doc.RootElement.TryGetProperty("Id", out p)) && p.TryGetGuid(out var id))
                {
                    return new StudyTaskDto(
                        id,
                        request.Title.Trim(),
                        request.Notes?.Trim(),
                        request.SubjectId,
                        null,
                        "Todo",
                        string.IsNullOrWhiteSpace(request.Priority) ? "Normal" : request.Priority,
                        request.DueDateLocal,
                        request.EstimatedMinutes,
                        false,
                        false,
                        DateTime.UtcNow,
                        null,
                        request.PlannedForLocalDate);
                }
            }
            catch { }
        }

        return new StudyTaskDto(
            Guid.NewGuid(),
            request.Title.Trim(),
            request.Notes?.Trim(),
            request.SubjectId,
            null,
            "Todo",
            string.IsNullOrWhiteSpace(request.Priority) ? "Normal" : request.Priority,
            request.DueDateLocal,
            request.EstimatedMinutes,
            false,
            false,
            DateTime.UtcNow,
            null,
            request.PlannedForLocalDate);
    }

    public async Task<StudyTaskDto> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.UpdateTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to update task.");
        }

        return new StudyTaskDto(
            request.Id,
            request.Title.Trim(),
            request.Notes?.Trim(),
            request.SubjectId,
            null,
            "Todo",
            string.IsNullOrWhiteSpace(request.Priority) ? "Normal" : request.Priority,
            request.DueDateLocal,
            request.EstimatedMinutes,
            false,
            false,
            DateTime.UtcNow,
            null,
            request.PlannedForLocalDate);
    }

    public async Task<StudyTaskDto> CompleteTaskAsync(CompleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CompleteTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to complete task.");
        }

        return new StudyTaskDto(
            request.Id,
            string.Empty,
            null,
            null,
            null,
            "Done",
            "Normal",
            null,
            null,
            false,
            false,
            DateTime.UtcNow,
            DateTime.UtcNow,
            null);
    }

    public async Task<StudyTaskDto> ReopenTaskAsync(ReopenTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ReopenTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to reopen task.");
        }

        return new StudyTaskDto(
            request.Id,
            string.Empty,
            null,
            null,
            null,
            "Todo",
            "Normal",
            null,
            null,
            false,
            false,
            DateTime.UtcNow,
            null,
            null);
    }

    public async Task<StudyTaskDto> StartTaskAsync(StartTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.StartTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to start task.");
        }

        return new StudyTaskDto(
            request.Id,
            string.Empty,
            null,
            null,
            null,
            "InProgress",
            "Normal",
            null,
            null,
            false,
            false,
            DateTime.UtcNow,
            null,
            null);
    }

    public async Task<StudyTaskDto> ArchiveTaskAsync(ArchiveTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ArchiveTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to archive task.");
        }

        return new StudyTaskDto(
            request.Id,
            string.Empty,
            null,
            null,
            null,
            !request.Restore ? "Archived" : "Todo",
            "Normal",
            null,
            null,
            !request.Restore,
            false,
            DateTime.UtcNow,
            null,
            null);
    }

    public async Task<StudyTaskDto> DeleteTaskAsync(SoftDeleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.SoftDeleteTaskOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to soft-delete task.");
        }

        return new StudyTaskDto(
            request.Id,
            string.Empty,
            null,
            null,
            null,
            "Archived",
            "Normal",
            null,
            null,
            false,
            true,
            DateTime.UtcNow,
            null,
            null);
    }

    public async Task<IReadOnlyList<FocusProfileDto>> ListFocusProfilesAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListFocusProfilesOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to list focus profiles.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return Array.Empty<FocusProfileDto>();
        }

        return JsonSerializer.Deserialize<List<FocusProfileDto>>(response.PayloadJson, IpcFrameCodec.SerializerOptions) ?? (IReadOnlyList<FocusProfileDto>)Array.Empty<FocusProfileDto>();
    }

    public async Task<FocusSessionDto?> GetActiveFocusSessionAsync(CancellationToken cancellationToken = default)
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetActiveFocusSessionOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to get active focus session.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return null;
        }

        return JsonSerializer.Deserialize<FocusSessionDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
    }

    public async Task<Guid> StartFocusSessionAsync(StartFocusSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to start focus session.");
        }

        if (!string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            using var doc = JsonDocument.Parse(response.PayloadJson);
            if ((doc.RootElement.TryGetProperty("id", out var p) || doc.RootElement.TryGetProperty("Id", out p)) && p.TryGetGuid(out var id))
            {
                return id;
            }
        }

        throw new AgentException(IpcErrors.OperationFailed, "Agent did not return a valid session ID.");
    }

    public async Task ApplyFocusCheckpointAsync(ApplyFocusCheckpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplyCheckpointOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to apply focus checkpoint.");
        }
    }

    public async Task<SessionOutcomeDto> EndFocusEarlyAsync(EndFocusEarlyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.EndFocusEarlyOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to end focus session early.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            throw new AgentException(IpcErrors.OperationFailed, "Empty payload returned from end focus early.");
        }

        return JsonSerializer.Deserialize<SessionOutcomeDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            ?? throw new AgentException(IpcErrors.OperationFailed, "Failed to deserialize session outcome.");
    }

    public async Task<SessionOutcomeDto> CompleteFocusSessionAsync(CompleteFocusSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadJson = JsonSerializer.Serialize(request, IpcFrameCodec.SerializerOptions);
        var ipcRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CompleteFocusSessionOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(ipcRequest, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to complete focus session.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            throw new AgentException(IpcErrors.OperationFailed, "Empty payload returned from complete focus session.");
        }

        return JsonSerializer.Deserialize<SessionOutcomeDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            ?? throw new AgentException(IpcErrors.OperationFailed, "Failed to deserialize session outcome.");
    }

    public async Task<IReadOnlyList<FocusSessionDto>> ListFocusSessionHistoryAsync(int maxCount = 50, CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(new { maxCount }, IpcFrameCodec.SerializerOptions);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListSessionHistoryOperation,
            DateTime.UtcNow,
            payloadJson);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            throw new AgentException(response.ErrorCode ?? IpcErrors.OperationFailed, response.ErrorMessage ?? "Failed to list session history.");
        }

        if (string.IsNullOrWhiteSpace(response.PayloadJson) || response.PayloadJson == "null")
        {
            return Array.Empty<FocusSessionDto>();
        }

        return JsonSerializer.Deserialize<List<FocusSessionDto>>(response.PayloadJson, IpcFrameCodec.SerializerOptions) ?? (IReadOnlyList<FocusSessionDto>)Array.Empty<FocusSessionDto>();
    }

    // -------------------------------------------------------------------------------------
    // IRecoveryClient Implementation
    // -------------------------------------------------------------------------------------

    public async Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            requestId,
            Guid.NewGuid().ToString("D"),
            IpcProtocol.RecoveryGetStateOperation,
            DateTime.UtcNow);

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            return RecoveryResponse.Fail(
                response.RequestId,
                response.ErrorCode ?? RecoveryErrors.AgentUnavailable,
                response.ErrorMessage ?? "Recovery state request failed.");
        }

        var state = !string.IsNullOrWhiteSpace(response.PayloadJson)
            ? JsonSerializer.Deserialize<RecoveryState>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            : null;

        return RecoveryResponse.Ok(response.RequestId, state!);
    }

    public async Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var payloadJson = JsonSerializer.Serialize(new { sessionId }, IpcFrameCodec.SerializerOptions);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            requestId,
            Guid.NewGuid().ToString("D"),
            IpcProtocol.RecoveryResumeOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            return RecoveryResponse.Fail(
                response.RequestId,
                response.ErrorCode ?? RecoveryErrors.AgentUnavailable,
                response.ErrorMessage ?? "Recovery resume request failed.");
        }

        var state = !string.IsNullOrWhiteSpace(response.PayloadJson)
            ? JsonSerializer.Deserialize<RecoveryState>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            : null;

        return RecoveryResponse.Ok(response.RequestId, state!);
    }

    public async Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var payloadJson = JsonSerializer.Serialize(new { sessionId }, IpcFrameCodec.SerializerOptions);
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            requestId,
            Guid.NewGuid().ToString("D"),
            IpcProtocol.RecoveryDiscardOperation,
            DateTime.UtcNow,
            payloadJson,
            IdempotencyKey: Guid.NewGuid().ToString("D"));

        var response = await SendRequestInternalAsync(request, cancellationToken);
        if (!response.Success)
        {
            return RecoveryResponse.Fail(
                response.RequestId,
                response.ErrorCode ?? RecoveryErrors.AgentUnavailable,
                response.ErrorMessage ?? "Recovery discard request failed.");
        }

        var state = !string.IsNullOrWhiteSpace(response.PayloadJson)
            ? JsonSerializer.Deserialize<RecoveryState>(response.PayloadJson, IpcFrameCodec.SerializerOptions)
            : null;

        return RecoveryResponse.Ok(response.RequestId, state!);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gate.WaitAsync();
        try
        {
            DisposePipe();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
