using System.Buffers.Binary;
using System.Text.Json;

namespace BoundlessStudy.Contracts.Ipc;

/// <summary>
/// Core IPC protocol constants and operation definitions (system-architecture.md §7.2; ADR-0005).
/// Length-prefixed JSON frames over message-mode or byte-stream Named Pipes.
/// Max frame size is 1 MB.
/// </summary>
public static class IpcProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 1024 * 1024; // 1 MB per architecture §7.2
    public const string PipeNamePrefix = "BoundlessStudy.UI.";

    public static string PipeNameForUser(string userSid) => PipeNamePrefix + userSid;

    // Operations
    public const string PingOperation = "ui.ping";
    public const string GetProfileProgressOperation = "ui.getProfileProgress";

    public const string ListSubjectsOperation = "subjects.list";
    public const string CreateSubjectOperation = "subjects.create";
    public const string UpdateSubjectOperation = "subjects.update";
    public const string ArchiveSubjectOperation = "subjects.archive";

    public const string ListTasksOperation = "tasks.list";
    public const string CreateTaskOperation = "tasks.create";
    public const string UpdateTaskOperation = "tasks.update";
    public const string CompleteTaskOperation = "tasks.complete";
    public const string ReopenTaskOperation = "tasks.reopen";
    public const string StartTaskOperation = "tasks.start";
    public const string ArchiveTaskOperation = "tasks.archive";
    public const string SoftDeleteTaskOperation = "tasks.softDelete";

    public const string ListFocusProfilesOperation = "focusProfiles.list";
    public const string GetActiveFocusSessionOperation = "focus.getActive";
    public const string StartFocusSessionOperation = "focus.start";
    public const string ApplyCheckpointOperation = "focus.checkpoint";
    public const string EndFocusEarlyOperation = "focus.endEarly";
    public const string CompleteFocusSessionOperation = "focus.complete";
    public const string ListSessionHistoryOperation = "focus.listHistory";

    public const string RecoveryGetStateOperation = "recovery.getState";
    public const string RecoveryResumeOperation = "recovery.resume";
    public const string RecoveryDiscardOperation = "recovery.discard";

    // Bridge Pipe
    public const string BridgePipeNamePrefix = "BoundlessStudy.Bridge.";
    public static string BridgePipeNameForUser(string userSid) => BridgePipeNamePrefix + userSid;

    // Blocking operations
    public const string ListBlockedAppsOperation = "blocking.apps.list";
    public const string AddBlockedAppOperation = "blocking.apps.add";
    public const string RemoveBlockedAppOperation = "blocking.apps.remove";

    public const string ListBlockedSitesOperation = "blocking.sites.list";
    public const string AddBlockedSiteOperation = "blocking.sites.add";
    public const string RemoveBlockedSiteOperation = "blocking.sites.remove";

    public const string ListAllowedSitesOperation = "blocking.allowedSites.list";
    public const string AddAllowedSiteOperation = "blocking.allowedSites.add";
    public const string RemoveAllowedSiteOperation = "blocking.allowedSites.remove";

    public const string GetProtectionStatusOperation = "blocking.protectionStatus";
    public const string GrantExceptionOperation = "blocking.grantException";
    public const string RecordViolationOperation = "blocking.recordViolation";
    public const string GetApplicationInterventionOperation = "blocking.applicationIntervention.get";
    public const string ApplicationOverlayAckOperation = "blocking.applicationOverlay.ack";

    // Bridge / Native Messaging Operations
    public const string BridgeHelloOperation = "bridge.hello";
    public const string BridgeHeartbeatOperation = "bridge.heartbeat";
    public const string BridgeGetPolicyOperation = "bridge.getPolicy";
    public const string BridgeReportViolationOperation = "bridge.reportViolation";
    public const string BridgePolicyAppliedOperation = "bridge.policyApplied";

    // System state operations
    public const string SystemStateNotificationOperation = "system.stateChanged";
}

/// <summary>Standard error codes returned across the UI IPC boundary.</summary>
public static class IpcErrors
{
    public const string InvalidRequest = "IPC_INVALID_REQUEST";
    public const string UnsupportedVersion = "IPC_UNSUPPORTED_VERSION";
    public const string AgentUnavailable = "AGENT_UNAVAILABLE";
    public const string ProtocolFailure = "IPC_PROTOCOL_FAILURE";
    public const string UnauthorizedClient = "IPC_UNAUTHORIZED_CLIENT";
    public const string Timeout = "IPC_TIMEOUT";
    public const string OperationFailed = "OPERATION_FAILED";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string TaskNotFound = "TASK_NOT_FOUND";
    public const string SubjectNotFound = "SUBJECT_NOT_FOUND";
    public const string RecoverableSessionPending = "RECOVERABLE_SESSION_PENDING";
    public const string TargetProtected = "BLOCK_TARGET_PROTECTED";
    public const string ExceptionLimitReached = "EXCEPTION_LIMIT_REACHED";
    public const string SessionNotActive = "SESSION_NOT_ACTIVE";
}

/// <summary>Length-prefixed JSON envelope for UI-to-agent requests.</summary>
public sealed record IpcRequest(
    int ProtocolVersion,
    string RequestId,
    string CorrelationId,
    string Operation,
    DateTime TimestampUtc,
    string? PayloadJson = null,
    string? IdempotencyKey = null);

/// <summary>Length-prefixed JSON envelope for agent-to-UI responses.</summary>
public sealed record IpcResponse(
    int ProtocolVersion,
    string RequestId,
    string CorrelationId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    string? PayloadJson,
    DateTime TimestampUtc)
{
    public static IpcResponse Ok(string requestId, string correlationId, string? payloadJson = null) =>
        new(IpcProtocol.ProtocolVersion, requestId, correlationId, true, null, null, payloadJson, DateTime.UtcNow);

    public static IpcResponse Fail(string requestId, string correlationId, string errorCode, string errorMessage, string? payloadJson = null) =>
        new(IpcProtocol.ProtocolVersion, requestId, correlationId, false, errorCode, errorMessage, payloadJson, DateTime.UtcNow);
}

/// <summary>Serialization and frame encoding for the length-prefixed IPC protocol.</summary>
public static class IpcFrameCodec
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteRequestAsync(Stream stream, IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        await WriteFrameAsync(stream, bytes, cancellationToken);
    }

    public static async Task WriteResponseAsync(Stream stream, IpcResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);
        await WriteFrameAsync(stream, bytes, cancellationToken);
    }

    public static async Task<IpcRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken);
        if (bytes is null) return null;
        try
        {
            return JsonSerializer.Deserialize<IpcRequest>(bytes, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The IPC request frame is not valid JSON.", ex);
        }
    }

    public static async Task<IpcResponse?> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken);
        if (bytes is null) return null;
        try
        {
            return JsonSerializer.Deserialize<IpcResponse>(bytes, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The IPC response frame is not valid JSON.", ex);
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException("The frame is empty or exceeds the maximum allowed size (1 MB).");
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
        if (read == 0) return null;
        if (read != prefix.Length)
        {
            await stream.ReadExactlyAsync(prefix.AsMemory(read), cancellationToken);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > IpcProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"The frame length ({length} bytes) is outside the allowed range (1 .. {IpcProtocol.MaxFrameBytes}).");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}

// -----------------------------------------------------------------------------------------
// Typed DTOs exchanged across IPC
// -----------------------------------------------------------------------------------------

public sealed record SubjectDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsArchived,
    DateTime CreatedAtUtc);

public sealed record CreateSubjectRequest(string Name, string? Description);
public sealed record UpdateSubjectRequest(Guid Id, string Name, string? Description);
public sealed record ArchiveSubjectRequest(Guid Id, bool Restore);

public sealed record StudyTaskDto(
    Guid Id,
    string Title,
    string? Notes,
    Guid? SubjectId,
    string? SubjectName,
    string Status,
    string Priority,
    DateOnly? DueDateLocal,
    int? EstimatedMinutes,
    bool IsArchived,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateOnly? PlannedForLocalDate);

public sealed record CreateTaskRequest(
    string Title,
    string? Notes,
    Guid? SubjectId,
    DateOnly? DueDateLocal,
    int? EstimatedMinutes,
    string Priority,
    DateOnly? PlannedForLocalDate);

public sealed record UpdateTaskRequest(
    Guid Id,
    string Title,
    string? Notes,
    Guid? SubjectId,
    DateOnly? DueDateLocal,
    int? EstimatedMinutes,
    string Priority,
    DateOnly? PlannedForLocalDate);

public sealed record CompleteTaskRequest(Guid Id);
public sealed record ReopenTaskRequest(Guid Id);
public sealed record StartTaskRequest(Guid Id);
public sealed record ArchiveTaskRequest(Guid Id, bool Restore);
public sealed record SoftDeleteTaskRequest(Guid Id);

public sealed record FocusProfileDto(
    string Code,
    string DisplayName,
    string EarlyExitMode,
    int XpPerActiveMinute,
    int MaxExceptionsPerSession,
    bool IsEnabled);

public sealed record FocusSessionDto(
    Guid Id,
    string ProfileCode,
    int PlannedFocusSeconds,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    string State,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string? TaskTitleSnapshot,
    string? SubjectNameSnapshot,
    int XpEarned,
    bool IsInterrupted,
    bool IsIdle = false,
    int IdleDurationSeconds = 0);

public sealed record StartFocusSessionRequest(
    string ProfileCode,
    int PlannedFocusSeconds,
    Guid? TaskId);

public sealed record ApplyFocusCheckpointRequest(
    Guid SessionId,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    int IdleSeconds,
    int ProtectionGapSeconds,
    int ViolationCount,
    int ExceptionCount,
    int RecoveryCount);

public sealed record EndFocusEarlyRequest(
    Guid SessionId,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    int IdleSeconds,
    int ProtectionGapSeconds,
    string EndReason);

public sealed record CompleteFocusSessionRequest(
    Guid SessionId,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    int IdleSeconds,
    int ProtectionGapSeconds);

public sealed record ProfileProgressDto(
    long TotalXp,
    int Level,
    int CurrentStreakDays,
    int LongestStreakDays,
    int QualifiedSessionCount,
    int StrictQualifiedSessionCount,
    int CompletedTaskCount,
    DateOnly TodayLocal,
    int SessionXpToday,
    int SessionCapRemainingToday,
    int DailySessionXpCap);

public sealed record SessionOutcomeDto(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsCompleted,
    int AwardXp,
    long TotalXp,
    int LevelBefore,
    int LevelAfter,
    bool StreakQualified,
    IReadOnlyList<string> UnlockedAchievements);

public sealed record BlockedAppDto(
    Guid Id,
    string DisplayName,
    string? NormalizedExecutablePath,
    string NormalizedExecutableName,
    bool IsEnabled,
    DateTime CreatedAtUtc);

public sealed record AddBlockedAppRequest(
    string DisplayName,
    string? ExecutablePath,
    string? ExecutableName);

public sealed record RemoveBlockedAppRequest(Guid Id);

public sealed record BlockedSiteDto(
    Guid Id,
    string NormalizedDomain,
    string? DisplayName,
    bool IsEnabled,
    DateTime CreatedAtUtc);

public sealed record AddBlockedSiteRequest(
    string Domain,
    string? DisplayName);

public sealed record RemoveBlockedSiteRequest(Guid Id);

public sealed record AllowedSiteDto(
    Guid Id,
    string NormalizedDomain,
    string? DisplayName,
    bool IsEnabled,
    DateTime CreatedAtUtc);

public sealed record AddAllowedSiteRequest(
    string Domain,
    string? DisplayName);

public sealed record RemoveAllowedSiteRequest(Guid Id);

public sealed record GrantExceptionRequest(
    Guid FocusSessionId,
    string TargetType,
    string TargetId,
    string ReasonCategory);

public sealed record ProtectionStatusDto(
    string Status,
    bool IsAppGuardActive,
    bool IsBrowserGuardianActive,
    int BlockedAppCount,
    int BlockedSiteCount,
    int AllowedSiteCount,
    DateTime? LastBrowserHeartbeatUtc,
    string? DegradedReason);

/// <summary>Current Agent-owned app intervention request for the Desktop overlay.</summary>
public sealed record ApplicationInterventionDto(
    string State,
    Guid? FocusSessionId,
    string? TargetKey,
    string? DisplayName,
    string? Reason,
    string EnforcementStatus,
    IReadOnlyList<string> AvailableActions,
    DateTime UpdatedAtUtc,
    WindowBoundsDto? TargetBounds = null);

/// <summary>Foreground target bounds in physical screen pixels; UI converts them to DIPs.</summary>
public sealed record WindowBoundsDto(int X, int Y, int Width, int Height);

/// <summary>Desktop acknowledgement after attempting to show or dismiss the real overlay.</summary>
public sealed record ApplicationOverlayAckRequest(
    Guid FocusSessionId,
    string TargetKey,
    bool IsVisible,
    string? ErrorCode = null,
    long? OverlayWindowHandle = null);

public sealed record BrowserPolicyDto(
    Guid? FocusSessionId,
    bool IsActive,
    DateTime? ExpiresAtUtc,
    IReadOnlyList<string> BlockedDomains,
    IReadOnlyList<string> AllowedDomains);

public sealed record BridgeHelloRequest(
    string ExtensionId,
    string Version,
    string BrowserFamily,
    int ProtocolVersion);

public sealed record BridgeHeartbeatRequest(
    string ExtensionId,
    string BrowserFamily,
    Guid? ActiveSessionId);

public sealed record BridgeHeartbeatResponse(Guid? ActiveSessionId);

public sealed record BridgeViolationReportRequest(
    Guid FocusSessionId,
    string BlockedDomain,
    DateTime OccurredAtUtc);

/// <summary>Browser acknowledgement that DNR rules were actually applied (not merely delivered).</summary>
public sealed record BridgePolicyAppliedRequest(
    string ExtensionId,
    string BrowserFamily,
    Guid? FocusSessionId,
    bool IsApplied,
    DateTime ReportedAtUtc,
    string? ErrorCode);

public sealed record SystemStateNotificationRequest(
    string StateType,
    DateTime OccurredAtUtc);

