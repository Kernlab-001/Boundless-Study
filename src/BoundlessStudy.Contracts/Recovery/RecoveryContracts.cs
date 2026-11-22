using System.Buffers.Binary;
using System.Text.Json;

namespace BoundlessStudy.Contracts.Recovery;

/// <summary>
/// Minimal, versioned UI-to-agent recovery protocol for the A6 Resume/Discard
/// workflow (system-architecture.md section 7.2; ADR-0005). Unknown JSON fields are
/// ignored; unknown protocol versions and operations are rejected before dispatch.
/// </summary>
public static class RecoveryProtocol
{
    public const int ProtocolVersion = 1;

    /// <summary>Bounded frame size for the recovery-only protocol (64 KiB).</summary>
    public const int MaxFrameBytes = 64 * 1024;

    public const string PipeNamePrefix = "BoundlessStudy.UI.";

    public const string GetStateOperation = "recovery.getState";
    public const string ResumeOperation = "recovery.resume";
    public const string DiscardOperation = "recovery.discard";

    public static string PipeNameForUser(string userSid) => PipeNamePrefix + userSid;
}

/// <summary>One request on the UI pipe. <c>RequestId</c> must be a GUID string.</summary>
public sealed record RecoveryRequest(
    int ProtocolVersion,
    string RequestId,
    string Operation,
    Guid? SessionId);

/// <summary>Read-only recovery information shown by the UI.</summary>
public sealed record RecoveryPendingSession(
    Guid SessionId,
    string ProfileCode,
    string? TaskTitle,
    DateTime StartedAtUtc,
    DateTime? InterruptedAtUtc,
    DateTime ExpiresAtUtc,
    long AgeSeconds,
    bool IsExpired,
    int PlannedFocusSeconds,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    DateTime? EffectiveInterruptionAnchorUtc = null,
    long? RemainingSeconds = null)
{
    /// <summary>
    /// Anchor timestamp for unresolved interruption age.
    /// Falls back to InterruptedAtUtc or StartedAtUtc.
    /// </summary>
    public DateTime AnchorUtc => EffectiveInterruptionAnchorUtc ?? InterruptedAtUtc ?? StartedAtUtc;

    /// <summary>
    /// Remaining seconds until automatic abandonment at 24 hours.
    /// Clamped to 0 when expired.
    /// </summary>
    public long EffectiveRemainingSeconds => RemainingSeconds ?? (IsExpired
        ? 0
        : Math.Max(0, (long)(ExpiresAtUtc - AnchorUtc).TotalSeconds - AgeSeconds));

    /// <summary>Remaining time as a TimeSpan.</summary>
    public TimeSpan RemainingTime => TimeSpan.FromSeconds(EffectiveRemainingSeconds);

    /// <summary>Age of the unresolved interruption as a TimeSpan.</summary>
    public TimeSpan Age => TimeSpan.FromSeconds(AgeSeconds);
}

/// <summary>The single local profile and its optional pending interruption.</summary>
public sealed record RecoveryState(Guid ProfileId, RecoveryPendingSession? Pending);

/// <summary>One response per request. Errors never carry a committed mutation.</summary>
public sealed record RecoveryResponse(
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    RecoveryState? State)
{
    public static RecoveryResponse Ok(string requestId, RecoveryState state) =>
        new(requestId, true, null, null, state);

    public static RecoveryResponse Fail(string requestId, string errorCode, string errorMessage, RecoveryState? state = null) =>
        new(requestId, false, errorCode, errorMessage, state);
}

/// <summary>Stable recovery-only error codes for the UI.</summary>
public static class RecoveryErrors
{
    public const string InvalidRequest = "RECOVERY_INVALID_REQUEST";
    public const string UnsupportedVersion = "RECOVERY_UNSUPPORTED_VERSION";
    public const string AgentUnavailable = "AGENT_UNAVAILABLE";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionNotPending = "SESSION_NOT_PENDING";
    public const string ProtocolFailure = "RECOVERY_PROTOCOL_FAILURE";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string RecoverableSessionPending = "RECOVERABLE_SESSION_PENDING";
}

/// <summary>Schema validation that runs on both ends before any domain dispatch.</summary>
public static class RecoveryRequestValidator
{
    public static bool TryValidate(RecoveryRequest? request, out string error)
    {
        if (request is null)
        {
            error = "The request is empty.";
            return false;
        }

        if (request.ProtocolVersion != RecoveryProtocol.ProtocolVersion)
        {
            error = $"Unsupported protocol version {request.ProtocolVersion}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length > 128
            || !Guid.TryParse(request.RequestId, out _))
        {
            error = "RequestId must be a GUID string of at most 128 characters.";
            return false;
        }

        switch (request.Operation)
        {
            case RecoveryProtocol.GetStateOperation:
                if (request.SessionId is not null)
                {
                    error = "The state query must not name a session.";
                    return false;
                }

                break;
            case RecoveryProtocol.ResumeOperation:
            case RecoveryProtocol.DiscardOperation:
                if (request.SessionId is not { } sessionId || sessionId == Guid.Empty)
                {
                    error = "A session id is required for this operation.";
                    return false;
                }

                break;
            default:
                error = "Unknown recovery operation.";
                return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Result of reading one request frame without throwing for malformed JSON.
/// <see cref="RequestId"/> is recovered from the payload when possible so a server
/// can answer with <see cref="RecoveryErrors.ProtocolFailure"/> before closing.
/// </summary>
public sealed record RecoveryRequestFrameReadResult(
    RecoveryRequest? Request,
    string? RequestId,
    bool IsMalformed,
    string? FailureMessage);

/// <summary>
/// Length-prefixed JSON frames: four-byte little-endian length, then UTF-8 JSON.
/// The reader enforces the frame bound before allocating. A clean EOF returns null.
/// </summary>
public static class RecoveryFrameCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        Stream stream,
        RecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions), cancellationToken);
    }

    public static async Task WriteAsync(
        Stream stream,
        RecoveryResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions), cancellationToken);
    }

    public static async Task<RecoveryRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecoveryRequest>(payload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The recovery request frame is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Reads one request frame and reports malformed JSON instead of throwing. A
    /// malformed frame may still expose its request id so the caller can answer and
    /// then close. Length-prefix violations and a clean EOF keep their original
    /// behavior (<see cref="InvalidDataException"/> and null respectively).
    /// </summary>
    public static async Task<RecoveryRequestFrameReadResult?> ReadRequestFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<RecoveryRequest>(payload, SerializerOptions);
            if (request is null)
            {
                return new RecoveryRequestFrameReadResult(
                    null,
                    null,
                    true,
                    "The recovery request frame is empty.");
            }

            return new RecoveryRequestFrameReadResult(request, request.RequestId, false, null);
        }
        catch (JsonException)
        {
            return new RecoveryRequestFrameReadResult(
                null,
                TryReadRequestId(payload),
                true,
                "The recovery request frame is malformed.");
        }
    }

    private static string? TryReadRequestId(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "requestId", "RequestId" })
            {
                if (root.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(value.GetString(), out _))
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // The payload is not JSON at all; no request id can be recovered.
        }

        return null;
    }

    public static async Task<RecoveryResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecoveryResponse>(payload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The recovery response frame is not valid JSON.", ex);
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > RecoveryProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException("The recovery frame is empty or exceeds the maximum size.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var read = await stream.ReadAsync(prefix, cancellationToken);
        if (read == 0)
        {
            return null;
        }

        if (read != prefix.Length)
        {
            await stream.ReadExactlyAsync(prefix.AsMemory(read), cancellationToken);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > RecoveryProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException("The recovery frame length is outside the allowed range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}
