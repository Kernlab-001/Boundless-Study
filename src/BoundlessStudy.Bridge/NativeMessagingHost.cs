using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.NativeMessaging;

namespace BoundlessStudy.Bridge;

/// <summary>
/// Native Messaging Host process translating Chrome and Edge extension Native Messaging frames
/// into the Boundless Study Agent IPC Named Pipe protocol (system-architecture.md §7.3, ADR-0004).
/// Reads length-prefixed JSON from standard input and writes length-prefixed JSON to standard output.
/// </summary>
public sealed class NativeMessagingHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _ipcSender;
    private readonly string _primaryPipeName;
    private readonly string _fallbackPipeName;

    public NativeMessagingHost(
        Stream stdin,
        Stream stdout,
        Func<IpcRequest, CancellationToken, Task<IpcResponse>>? ipcSender = null,
        string? primaryPipeName = null,
        string? fallbackPipeName = null)
    {
        _stdin = stdin ?? throw new ArgumentNullException(nameof(stdin));
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));

        var userSid = ResolveCurrentUserSid();
        _primaryPipeName = NormalizePipeName(primaryPipeName ?? IpcProtocol.BridgePipeNameForUser(userSid));
        _fallbackPipeName = NormalizePipeName(fallbackPipeName ?? ("BoundlessStudy.Agent." + userSid));

        _ipcSender = ipcSender ?? DefaultSendIpcAsync;
    }

    public string PrimaryPipeName => _primaryPipeName;
    public string FallbackPipeName => _fallbackPipeName;

    /// <summary>
    /// Runs the message processing loop until standard input reaches EOF or cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NativeInboundMessage? inbound;
            try
            {
                inbound = await NativeFrameCodec.ReadMessageAsync(_stdin, cancellationToken);
                if (inbound is null)
                {
                    // EOF on standard input: browser closed the native messaging channel
                    break;
                }
            }
            catch (InvalidDataException ex)
            {
                // Reject malformed native messaging frames per protocol specification
                var errorResponse = new NativeOutboundMessage(
                    Type: NativeMessagingProtocol.TypeError,
                    Success: false,
                    ErrorMessage: "Malformed native messaging frame: " + ex.Message);

                try
                {
                    await NativeFrameCodec.WriteMessageAsync(_stdout, errorResponse, cancellationToken);
                }
                catch
                {
                    // Failed writing error response to stdout
                }
                break;
            }
            catch (Exception)
            {
                break;
            }

            var outbound = await ProcessMessageAsync(inbound, cancellationToken);
            try
            {
                await NativeFrameCodec.WriteMessageAsync(_stdout, outbound, cancellationToken);
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Processes a single inbound native message and produces the corresponding outbound response.
    /// </summary>
    public async Task<NativeOutboundMessage> ProcessMessageAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.Equals(message.ExtensionId, NativeMessagingProtocol.ExtensionId, StringComparison.Ordinal)
            || message.BrowserFamily is not ("chrome" or "edge"))
        {
            return new NativeOutboundMessage(
                Type: NativeMessagingProtocol.TypeError,
                Success: false,
                ErrorMessage: "The browser extension identity or browser family is unsupported.");
        }

        try
        {
            return message.Type switch
            {
                NativeMessagingProtocol.TypeHello => await HandleHelloAsync(message, cancellationToken),
                NativeMessagingProtocol.TypeHeartbeat => await HandleHeartbeatAsync(message, cancellationToken),
                NativeMessagingProtocol.TypeGetPolicy => await HandleGetPolicyAsync(message, cancellationToken),
                NativeMessagingProtocol.TypeReportViolation => await HandleReportViolationAsync(message, cancellationToken),
                NativeMessagingProtocol.TypePolicyApplied => await HandlePolicyAppliedAsync(message, cancellationToken),
                _ => new NativeOutboundMessage(
                    Type: NativeMessagingProtocol.TypeError,
                    Success: false,
                    ErrorMessage: $"Unsupported message type: '{message.Type}'.")
            };
        }
        catch (Exception)
        {
            return new NativeOutboundMessage(
                Type: NativeMessagingProtocol.TypeError,
                Success: false,
                ErrorMessage: "Failed to process message.");
        }
    }

    private async Task<NativeOutboundMessage> HandleHelloAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken)
    {
        var helloRequest = new BridgeHelloRequest(
            ExtensionId: message.ExtensionId!,
            Version: message.Version ?? "0.1.0",
            BrowserFamily: message.BrowserFamily!,
            ProtocolVersion: IpcProtocol.ProtocolVersion);

        var ipcRequest = new IpcRequest(
            ProtocolVersion: IpcProtocol.ProtocolVersion,
            RequestId: Guid.NewGuid().ToString("D"),
            CorrelationId: Guid.NewGuid().ToString("D"),
            Operation: IpcProtocol.BridgeHelloOperation,
            TimestampUtc: DateTime.UtcNow,
            PayloadJson: JsonSerializer.Serialize(helloRequest, JsonOptions));

        var ipcResponse = await _ipcSender(ipcRequest, cancellationToken);
        if (ipcResponse.Success)
        {
            return new NativeOutboundMessage(
                Type: NativeMessagingProtocol.TypeHelloAck,
                Success: true);
        }

        return new NativeOutboundMessage(
            Type: NativeMessagingProtocol.TypeHelloAck,
            Success: false,
            ErrorMessage: ipcResponse.Success ? null : "Hello rejected by agent.");
    }

    private async Task<NativeOutboundMessage> HandleHeartbeatAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken)
    {
        var heartbeatRequest = new BridgeHeartbeatRequest(
            ExtensionId: message.ExtensionId!,
            BrowserFamily: message.BrowserFamily!,
            ActiveSessionId: message.SessionId);

        var ipcRequest = new IpcRequest(
            ProtocolVersion: IpcProtocol.ProtocolVersion,
            RequestId: Guid.NewGuid().ToString("D"),
            CorrelationId: Guid.NewGuid().ToString("D"),
            Operation: IpcProtocol.BridgeHeartbeatOperation,
            TimestampUtc: DateTime.UtcNow,
            PayloadJson: JsonSerializer.Serialize(heartbeatRequest, JsonOptions));

        var ipcResponse = await _ipcSender(ipcRequest, cancellationToken);
        Guid? activeSessionId = null;
        if (ipcResponse.Success && !string.IsNullOrWhiteSpace(ipcResponse.PayloadJson))
        {
            try
            {
                activeSessionId = JsonSerializer.Deserialize<BridgeHeartbeatResponse>(ipcResponse.PayloadJson, JsonOptions)?.ActiveSessionId;
            }
            catch (JsonException)
            {
                return new NativeOutboundMessage(
                    Type: NativeMessagingProtocol.TypeHeartbeatAck,
                    Success: false,
                    ErrorMessage: "Agent returned an invalid heartbeat status.");
            }
        }

        return new NativeOutboundMessage(
            Type: NativeMessagingProtocol.TypeHeartbeatAck,
            Success: ipcResponse.Success,
            SessionId: activeSessionId,
            ErrorMessage: ipcResponse.Success ? null : "Heartbeat failed.");
    }

    private async Task<NativeOutboundMessage> HandleGetPolicyAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken)
    {
        var ipcRequest = new IpcRequest(
            ProtocolVersion: IpcProtocol.ProtocolVersion,
            RequestId: Guid.NewGuid().ToString("D"),
            CorrelationId: Guid.NewGuid().ToString("D"),
            Operation: IpcProtocol.BridgeGetPolicyOperation,
            TimestampUtc: DateTime.UtcNow);

        var ipcResponse = await _ipcSender(ipcRequest, cancellationToken);
        if (ipcResponse.Success && !string.IsNullOrWhiteSpace(ipcResponse.PayloadJson))
        {
            try
            {
                var policy = JsonSerializer.Deserialize<BrowserPolicyDto>(ipcResponse.PayloadJson, JsonOptions);
                if (policy is not null && IsValidPolicy(policy))
                {
                    return new NativeOutboundMessage(
                        Type: NativeMessagingProtocol.TypePolicy,
                        Success: true,
                        SessionId: policy.FocusSessionId,
                        BlockedDomains: policy.BlockedDomains,
                        AllowedDomains: policy.AllowedDomains,
                        ExpiresAtUtc: policy.ExpiresAtUtc);
                }
            }
            catch (JsonException)
            {
                return new NativeOutboundMessage(
                    Type: NativeMessagingProtocol.TypePolicy,
                    Success: false,
                    ErrorMessage: "Agent returned an invalid browser policy payload.");
            }
            catch (Exception) when (ipcResponse.Success)
            {
                return new NativeOutboundMessage(
                    Type: NativeMessagingProtocol.TypePolicy,
                    Success: false,
                    ErrorMessage: "Agent returned an invalid browser policy payload.");
            }
        }

        return new NativeOutboundMessage(
            Type: NativeMessagingProtocol.TypePolicy,
            Success: false,
            ErrorMessage: "Failed to retrieve browser policy.");
    }

    private async Task<NativeOutboundMessage> HandleReportViolationAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken)
    {
        var normalizedDomain = BrowserRuleGenerator.NormalizeDomain(message.Domain);
        if (!message.SessionId.HasValue || message.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(normalizedDomain)
            || !string.Equals(normalizedDomain, message.Domain, StringComparison.Ordinal))
        {
            return new NativeOutboundMessage(
                Type: NativeMessagingProtocol.TypeViolationAck,
                Success: false,
                ErrorMessage: "Violation report is invalid.");
        }

        var violationRequest = new BridgeViolationReportRequest(
            FocusSessionId: message.SessionId.Value,
            BlockedDomain: normalizedDomain,
            OccurredAtUtc: DateTime.UtcNow);

        var ipcRequest = new IpcRequest(
            ProtocolVersion: IpcProtocol.ProtocolVersion,
            RequestId: Guid.NewGuid().ToString("D"),
            CorrelationId: Guid.NewGuid().ToString("D"),
            Operation: IpcProtocol.BridgeReportViolationOperation,
            TimestampUtc: DateTime.UtcNow,
            PayloadJson: JsonSerializer.Serialize(violationRequest, JsonOptions));

        var ipcResponse = await _ipcSender(ipcRequest, cancellationToken);
        return new NativeOutboundMessage(
            Type: NativeMessagingProtocol.TypeViolationAck,
            Success: ipcResponse.Success,
            SessionId: message.SessionId,
            ErrorMessage: ipcResponse.Success ? null : "Violation report rejected.");
    }

    private async Task<NativeOutboundMessage> HandlePolicyAppliedAsync(
        NativeInboundMessage message,
        CancellationToken cancellationToken)
    {
        if (message.IsApplied is null || message.BrowserFamily is not ("chrome" or "edge"))
        {
            return new NativeOutboundMessage(
                Type: NativeMessagingProtocol.TypePolicyAppliedAck,
                Success: false,
                ErrorMessage: "Policy status is incomplete or the browser is unsupported.");
        }

        var request = new BridgePolicyAppliedRequest(
            ExtensionId: message.ExtensionId!,
            BrowserFamily: message.BrowserFamily!,
            FocusSessionId: message.SessionId,
            IsApplied: message.IsApplied.Value,
            ReportedAtUtc: DateTime.UtcNow,
            ErrorCode: NormalizePolicyErrorCode(message.ErrorCode));

        var ipcRequest = new IpcRequest(
            ProtocolVersion: IpcProtocol.ProtocolVersion,
            RequestId: Guid.NewGuid().ToString("D"),
            CorrelationId: Guid.NewGuid().ToString("D"),
            Operation: IpcProtocol.BridgePolicyAppliedOperation,
            TimestampUtc: DateTime.UtcNow,
            PayloadJson: JsonSerializer.Serialize(request, JsonOptions));

        var ipcResponse = await _ipcSender(ipcRequest, cancellationToken);
        return new NativeOutboundMessage(
            Type: NativeMessagingProtocol.TypePolicyAppliedAck,
            Success: ipcResponse.Success,
            SessionId: message.SessionId,
            ErrorMessage: ipcResponse.Success ? null : "Agent rejected the browser protection status.");
    }

    private static bool IsValidPolicy(BrowserPolicyDto policy)
    {
        if (policy.BlockedDomains is null || policy.AllowedDomains is null
            || policy.BlockedDomains.Count + policy.AllowedDomains.Count > 1000)
        {
            return false;
        }

        if (policy.IsActive && (!policy.FocusSessionId.HasValue || !policy.ExpiresAtUtc.HasValue))
        {
            return false;
        }
        if (!policy.IsActive && policy.FocusSessionId.HasValue) return false;

        return policy.BlockedDomains.Concat(policy.AllowedDomains).All(domain =>
            !string.IsNullOrWhiteSpace(domain)
            && string.Equals(BrowserRuleGenerator.NormalizeDomain(domain), domain, StringComparison.Ordinal));
    }

    private static string? NormalizePolicyErrorCode(string? value) => value switch
    {
        "AGENT_DISCONNECTED" or "DNR_UPDATE_FAILED" or "POLICY_INVALID" or "POLICY_EXPIRED" => value,
        null or "" => null,
        _ => "POLICY_INVALID"
    };

    private async Task<IpcResponse> DefaultSendIpcAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var candidatePipes = new[] { _primaryPipeName, _fallbackPipeName };

        foreach (var pipeName in candidatePipes)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                continue;
            }

            try
            {
                await using var pipeClient = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectCts.Token);

                await pipeClient.ConnectAsync(linkedCts.Token);
                await IpcFrameCodec.WriteRequestAsync(pipeClient, request, cancellationToken);
                var response = await IpcFrameCodec.ReadResponseAsync(pipeClient, cancellationToken);
                if (response is not null)
                {
                    return response;
                }
            }
            catch
            {
                // Try fallback pipe candidate
            }
        }

        return IpcResponse.Fail(
            requestId: request.RequestId,
            correlationId: request.CorrelationId,
            errorCode: IpcErrors.AgentUnavailable,
            errorMessage: "Boundless Study Agent IPC service is unavailable.");
    }

    private static string NormalizePipeName(string raw)
    {
        const string prefix = @"\\.\pipe\";
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return raw[prefix.Length..];
        }
        return raw;
    }

    public static string ResolveCurrentUserSid()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return WindowsIdentity.GetCurrent().User?.Value ?? "Default";
            }
            catch
            {
                // Fallback on platform or elevation error
            }
        }

        return "Default";
    }
}
