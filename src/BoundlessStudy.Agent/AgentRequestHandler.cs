using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Contracts.NativeMessaging;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Agent;

/// <summary>
/// Dispatches length-prefixed IPC operations to the application and domain layer (ADR-0005).
/// Sanitizes all error messages, enforces request validation, and maintains an idempotency cache.
/// </summary>
public sealed class AgentRequestHandler
{
    private readonly SqliteDataStore _store;
    private readonly ILocalClock _clock;
    private readonly ConcurrentDictionary<string, (IpcResponse Response, DateTime CachedAtUtc)> _idempotencyCache = new();
    private readonly ConcurrentDictionary<string, BrowserBridgeState> _browserBridges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastWebsiteViolationTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _idempotencyTtl = TimeSpan.FromHours(1);
    private ApplicationBlockingCoordinator? _applicationBlockingCoordinator;

    public AgentRequestHandler(SqliteDataStore store, ILocalClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void AttachApplicationBlockingCoordinator(ApplicationBlockingCoordinator coordinator) =>
        _applicationBlockingCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task<IpcResponse> HandleRequestAsync(
        IpcRequest? request,
        CancellationToken cancellationToken = default,
        AgentClientRole clientRole = AgentClientRole.Desktop)
    {
        if (request is null)
        {
            return IpcResponse.Fail(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                IpcErrors.InvalidRequest,
                "Request frame is empty or null.");
        }

        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("D") : request.RequestId;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("D") : request.CorrelationId;

        if (request.ProtocolVersion != IpcProtocol.ProtocolVersion)
        {
            return IpcResponse.Fail(
                requestId,
                correlationId,
                IpcErrors.UnsupportedVersion,
                $"Protocol version {request.ProtocolVersion} is not supported. Expected {IpcProtocol.ProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation))
        {
            return IpcResponse.Fail(
                requestId,
                correlationId,
                IpcErrors.InvalidRequest,
                "Operation is required.");
        }

        // Idempotency check for mutating requests
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            if (_idempotencyCache.TryGetValue(request.IdempotencyKey, out var cached))
            {
                if (cached.CachedAtUtc >= _clock.UtcNow - _idempotencyTtl)
                {
                    return cached.Response with
                    {
                        RequestId = requestId,
                        CorrelationId = correlationId,
                        TimestampUtc = _clock.UtcNow
                    };
                }

                _idempotencyCache.TryRemove(request.IdempotencyKey, out _);
            }
        }

        IpcResponse response;
        try
        {
            response = await DispatchOperationAsync(request, requestId, correlationId, clientRole, cancellationToken);
        }
        catch (DomainException ex)
        {
            response = IpcResponse.Fail(
                requestId,
                correlationId,
                ex.Code,
                SanitizeErrorMessage(ex.Message));
        }
        catch (JsonException)
        {
            response = IpcResponse.Fail(
                requestId,
                correlationId,
                IpcErrors.InvalidRequest,
                "The payload is not valid JSON.");
        }
        catch (PersistenceException ex)
        {
            response = IpcResponse.Fail(
                requestId,
                correlationId,
                IpcErrors.OperationFailed,
                $"Database error: {SanitizeErrorMessage(ex.Message)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = IpcResponse.Fail(
                requestId,
                correlationId,
                IpcErrors.OperationFailed,
                "An unexpected internal error occurred.");
        }

        // Store in idempotency cache if request had a key and was successful
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) && response.Success)
        {
            _idempotencyCache[request.IdempotencyKey] = (response, _clock.UtcNow);
            PruneIdempotencyCache();
        }

        return response;
    }

    private async Task<IpcResponse> DispatchOperationAsync(
        IpcRequest request,
        string requestId,
        string correlationId,
        AgentClientRole clientRole,
        CancellationToken cancellationToken)
    {
        var isBridgeOperation = request.Operation.StartsWith("bridge.", StringComparison.Ordinal);
        if ((clientRole == AgentClientRole.BrowserBridge) != isBridgeOperation)
        {
            return IpcResponse.Fail(requestId, correlationId, IpcErrors.UnauthorizedClient,
                "The requested operation is not available to this IPC client role.");
        }

        // 1. Standalone / non-database operations
        if (request.Operation == IpcProtocol.PingOperation)
        {
            return IpcResponse.Ok(requestId, correlationId, "{\"status\":\"ok\"}");
        }

        // 2. Database-backed operations
        await using var repos = _store.CreateRepositories();
        var localProfile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
        if (localProfile is null)
        {
            return IpcResponse.Fail(requestId, correlationId, IpcErrors.ProfileNotFound, "No local profile is present.");
        }

        var profileId = localProfile.Id;

        // Shared helpers for focus sessions
        var rewards = new RewardService(repos.RewardLedger, repos.Achievements, repos.UserAchievements, repos.RewardState, _clock);
        var recorder = new SessionEventRecorder(repos.SessionEvents, _clock);
        var recover = new RecoverFocusSessionHandler(repos.FocusSessions, repos.LocalProfiles, repos.UnitOfWork, rewards, _clock, recorder);
        var abandon = new AbandonFocusSessionHandler(repos.FocusSessions, _clock, recover);

        var blockingResponse = await TryHandleBlockingOperationAsync(
            request, requestId, correlationId, clientRole, repos, profileId, recorder, cancellationToken);
        if (blockingResponse is not null)
        {
            return blockingResponse;
        }

        switch (request.Operation)
        {
            // --- Profile Progress ---
            case IpcProtocol.GetProfileProgressOperation:
            {
                var handler = new GetProfileProgressHandler(repos.LocalProfiles, repos.RewardLedger, repos.RewardState, _clock);
                var progress = await handler.HandleAsync(profileId, cancellationToken);
                if (progress is null)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.ProfileNotFound, "Profile progress not found.");
                }

                var dto = new ProfileProgressDto(
                    progress.TotalXp,
                    progress.Level,
                    progress.CurrentStreakDays,
                    progress.LongestStreakDays,
                    progress.QualifiedSessionCount,
                    progress.StrictQualifiedSessionCount,
                    progress.CompletedTaskCount,
                    progress.TodayLocal,
                    progress.SessionXpToday,
                    progress.SessionCapRemainingToday,
                    progress.DailySessionXpCap);

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dto, IpcFrameCodec.SerializerOptions));
            }

            // --- Subjects ---
            case IpcProtocol.ListSubjectsOperation:
            {
                bool includeArchived = true;
                if (!string.IsNullOrWhiteSpace(request.PayloadJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(request.PayloadJson);
                        if (doc.RootElement.TryGetProperty("includeArchived", out var p) &&
                            (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
                        {
                            includeArchived = p.GetBoolean();
                        }
                    }
                    catch { }
                }

                var handler = new ListSubjectsHandler(repos.LocalProfiles, repos.Subjects);
                var list = await handler.HandleAsync(profileId, includeArchived, cancellationToken);
                var dtos = list.Select(s => new SubjectDto(s.Id, s.Name, s.Description, s.IsArchived, s.CreatedAtUtc)).ToList();
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dtos, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.CreateSubjectOperation:
            {
                var req = DeserializePayload<CreateSubjectRequest>(request.PayloadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.Name))
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Subject name is required.");
                }

                var handler = new CreateSubjectHandler(repos.LocalProfiles, repos.Subjects, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(profileId, req.Name, req.Description, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to create subject."));
                }

                var createdDto = new SubjectDto(result.Id!.Value, req.Name.Trim(), req.Description?.Trim(), false, _clock.UtcNow);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(createdDto, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.UpdateSubjectOperation:
            {
                var req = DeserializePayload<UpdateSubjectRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty || string.IsNullOrWhiteSpace(req.Name))
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Subject Id and Name are required.");
                }

                var handler = new UpdateSubjectHandler(repos.Subjects, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, req.Name, req.Description, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to update subject."));
                }

                var updatedDto = new SubjectDto(req.Id, req.Name.Trim(), req.Description?.Trim(), false, _clock.UtcNow);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(updatedDto, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.ArchiveSubjectOperation:
            {
                var req = DeserializePayload<ArchiveSubjectRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Subject Id is required.");
                }

                var handler = new ArchiveSubjectHandler(repos.Subjects, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, req.Restore, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to archive subject."));
                }

                var archivedDto = new SubjectDto(req.Id, string.Empty, null, !req.Restore, _clock.UtcNow);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(archivedDto, IpcFrameCodec.SerializerOptions));
            }

            // --- Tasks ---
            case IpcProtocol.ListTasksOperation:
            {
                var handler = new ListTasksHandler(repos.LocalProfiles, repos.StudyTasks, repos.Subjects);
                var tasks = await handler.HandleAsync(profileId, cancellationToken);
                var dtos = tasks.Select(t => new StudyTaskDto(
                    t.Id,
                    t.Title,
                    t.Notes,
                    t.SubjectId,
                    t.SubjectName,
                    t.Status.ToString(),
                    t.Priority.ToString(),
                    t.DueDateLocal,
                    t.EstimatedMinutes,
                    t.IsArchived,
                    t.IsDeleted,
                    t.CreatedAtUtc,
                    t.CompletedAtUtc,
                    t.PlannedForLocalDate)).ToList();

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dtos, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.CreateTaskOperation:
            {
                var req = DeserializePayload<CreateTaskRequest>(request.PayloadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.Title))
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task title is required.");
                }

                var priority = Enum.TryParse<TaskPriority>(req.Priority, true, out var p) ? p : TaskPriority.Normal;
                var handler = new CreateTaskHandler(repos.LocalProfiles, repos.StudyTasks, repos.Subjects, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(
                    profileId,
                    req.Title,
                    req.Notes,
                    req.SubjectId,
                    req.DueDateLocal,
                    req.EstimatedMinutes,
                    priority,
                    req.PlannedForLocalDate,
                    cancellationToken);

                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to create task."));
                }

                var createdTask = new StudyTaskDto(
                    result.Id!.Value,
                    req.Title.Trim(),
                    req.Notes?.Trim(),
                    req.SubjectId,
                    null,
                    "Todo",
                    priority.ToString(),
                    req.DueDateLocal,
                    req.EstimatedMinutes,
                    false,
                    false,
                    _clock.UtcNow,
                    null,
                    req.PlannedForLocalDate);

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(createdTask, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.UpdateTaskOperation:
            {
                var req = DeserializePayload<UpdateTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty || string.IsNullOrWhiteSpace(req.Title))
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id and Title are required.");
                }

                var priority = Enum.TryParse<TaskPriority>(req.Priority, true, out var p) ? p : TaskPriority.Normal;
                var handler = new UpdateTaskHandler(repos.StudyTasks, repos.Subjects, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(
                    req.Id,
                    req.Title,
                    req.Notes,
                    req.SubjectId,
                    req.DueDateLocal,
                    req.EstimatedMinutes,
                    priority,
                    req.PlannedForLocalDate,
                    cancellationToken);

                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to update task."));
                }

                var updatedTask = new StudyTaskDto(
                    req.Id,
                    req.Title.Trim(),
                    req.Notes?.Trim(),
                    req.SubjectId,
                    null,
                    "Todo",
                    priority.ToString(),
                    req.DueDateLocal,
                    req.EstimatedMinutes,
                    false,
                    false,
                    _clock.UtcNow,
                    null,
                    req.PlannedForLocalDate);

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(updatedTask, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.CompleteTaskOperation:
            {
                var req = DeserializePayload<CompleteTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id is required.");
                }

                var handler = new CompleteTaskHandler(repos.LocalProfiles, repos.StudyTasks, repos.UnitOfWork, rewards, _clock);
                var result = await handler.HandleAsync(req.Id, cancellationToken);
                var outcome = new SessionOutcomeDto(
                    result.Success,
                    result.Error?.Code,
                    SanitizeErrorMessage(result.Error?.Message),
                    true,
                    EconomyRules.TaskCompletionXp,
                    result.TotalXpAfter,
                    result.LevelBefore,
                    result.LevelAfter,
                    false,
                    Array.Empty<string>());

                if (!result.Success)
                {
                    return IpcResponse.Fail(
                        requestId,
                        correlationId,
                        MapErrorCode(result.Error?.Code),
                        SanitizeErrorMessage(result.Error?.Message ?? "Failed to complete task."),
                        JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
                }

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.ReopenTaskOperation:
            {
                var req = DeserializePayload<ReopenTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id is required.");
                }

                var handler = new ReopenTaskHandler(repos.StudyTasks, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to reopen task."));
                }

                return IpcResponse.Ok(requestId, correlationId);
            }

            case IpcProtocol.StartTaskOperation:
            {
                var req = DeserializePayload<StartTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id is required.");
                }

                var handler = new StartTaskHandler(repos.StudyTasks, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to start task."));
                }

                return IpcResponse.Ok(requestId, correlationId);
            }

            case IpcProtocol.ArchiveTaskOperation:
            {
                var req = DeserializePayload<ArchiveTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id is required.");
                }

                var handler = new ArchiveTaskHandler(repos.StudyTasks, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, req.Restore, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to archive task."));
                }

                return IpcResponse.Ok(requestId, correlationId);
            }

            case IpcProtocol.SoftDeleteTaskOperation:
            {
                var req = DeserializePayload<SoftDeleteTaskRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "Task Id is required.");
                }

                var handler = new SoftDeleteTaskHandler(repos.StudyTasks, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(req.Id, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to delete task."));
                }

                return IpcResponse.Ok(requestId, correlationId);
            }

            // --- Focus Profiles ---
            case IpcProtocol.ListFocusProfilesOperation:
            {
                var handler = new ListFocusProfilesHandler(repos.FocusProfiles);
                var list = await handler.HandleAsync(cancellationToken);
                var dtos = list.Select(p => new FocusProfileDto(
                    p.Code,
                    p.DisplayName,
                    p.EarlyExitMode,
                    p.XpPerActiveMinute,
                    p.MaxExceptionsPerSession,
                    p.IsEnabled)).ToList();

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dtos, IpcFrameCodec.SerializerOptions));
            }

            // --- Focus Sessions ---
            case IpcProtocol.GetActiveFocusSessionOperation:
            {
                var handler = new GetActiveFocusSessionHandler(repos.LocalProfiles, repos.FocusSessions);
                var s = await handler.HandleAsync(profileId, cancellationToken);
                if (s is null)
                {
                    return IpcResponse.Ok(requestId, correlationId, null);
                }

                // Idle becomes active after the approved five-minute threshold. Read the
                // append-only transition stream so the Desktop gets the same persisted state
                // that drives idle accounting, even after an Agent/UI reconnect.
                var idleEvents = await repos.SessionEvents.ListBySessionAsync(SessionType.Focus, s.Id, cancellationToken);
                var idleStart = idleEvents.LastOrDefault(e =>
                    e.EventType is SessionEventType.IdleStarted or SessionEventType.IdleEnded);
                var isIdle = idleStart?.EventType == SessionEventType.IdleStarted;
                var idleDurationSeconds = isIdle
                    ? Math.Max(0, (int)Math.Min(int.MaxValue, (_clock.UtcNow - idleStart!.OccurredAtUtc).TotalSeconds))
                    : 0;

                var dto = new FocusSessionDto(
                    s.Id,
                    s.ProfileCode,
                    s.PlannedFocusSeconds,
                    s.ElapsedSeconds,
                    s.ActiveStudySeconds,
                    s.State.ToString(),
                    s.StartedAtUtc,
                    s.EndedAtUtc,
                    s.TaskTitleSnapshot,
                    s.SubjectNameSnapshot,
                    s.XpEarned,
                    s.IsInterrupted,
                    isIdle,
                    idleDurationSeconds);

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dto, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.StartFocusSessionOperation:
            {
                var req = DeserializePayload<StartFocusSessionRequest>(request.PayloadJson);
                if (req is null || string.IsNullOrWhiteSpace(req.ProfileCode))
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "ProfileCode is required.");
                }

                var focusProfile = await repos.FocusProfiles.FindAsync(req.ProfileCode, cancellationToken);
                var configuredSites = await repos.BlockedSites.CountEnabledAsync(profileId, cancellationToken);
                if (focusProfile?.RequiresBrowserWhenSiteRulesExist == true
                    && configuredSites > 0
                    && !IsBrowserProtectionReady(null))
                {
                    return IpcResponse.Fail(requestId, correlationId, "BROWSER_PROTECTION_UNAVAILABLE",
                        "Strict focus cannot start until Chrome or Edge has connected and confirmed that its current policy was applied.");
                }

                var handler = new StartFocusSessionHandler(
                    repos.LocalProfiles,
                    repos.FocusProfiles,
                    repos.FocusSessions,
                    repos.BreakSessions,
                    repos.StudyTasks,
                    repos.Subjects,
                    repos.UnitOfWork,
                    _clock,
                    recorder,
                    abandon);

                var result = await handler.HandleAsync(profileId, req.ProfileCode, req.PlannedFocusSeconds, req.TaskId, cancellationToken);
                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to start focus session."));
                }

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new { id = result.Id }, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.ApplyCheckpointOperation:
            {
                var req = DeserializePayload<ApplyFocusCheckpointRequest>(request.PayloadJson);
                if (req is null || req.SessionId == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "SessionId is required.");
                }

                var checkpointSession = await repos.FocusSessions.FindAsync(req.SessionId, cancellationToken);
                if (checkpointSession is null || checkpointSession.LocalProfileId != profileId)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotFound, "The focus session does not exist.");
                if (checkpointSession.State != SessionState.Active)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotActive,
                        "Checkpoints are accepted only while the Agent confirms the session is active.");
                var eventsForCheckpoint = await repos.SessionEvents.ListBySessionAsync(SessionType.Focus, req.SessionId, cancellationToken);
                var lastIdle = eventsForCheckpoint.LastOrDefault(e =>
                    e.EventType is SessionEventType.IdleStarted or SessionEventType.IdleEnded);
                if (lastIdle?.EventType == SessionEventType.IdleStarted)
                    req = req with { ActiveStudySeconds = checkpointSession.ActiveStudySeconds };

                var handler = new ApplyFocusCheckpointHandler(repos.FocusSessions, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(
                    req.SessionId,
                    req.ElapsedSeconds,
                    req.ActiveStudySeconds,
                    req.IdleSeconds,
                    req.ProtectionGapSeconds,
                    req.ViolationCount,
                    req.ExceptionCount,
                    req.RecoveryCount,
                    cancellationToken);

                if (!result.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message ?? "Failed to apply checkpoint."));
                }

                return IpcResponse.Ok(requestId, correlationId);
            }

            case IpcProtocol.EndFocusEarlyOperation:
            {
                var req = DeserializePayload<EndFocusEarlyRequest>(request.PayloadJson);
                if (req is null || req.SessionId == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "SessionId is required.");
                }

                var endingSession = await repos.FocusSessions.FindAsync(req.SessionId, cancellationToken);
                if (endingSession is null || endingSession.LocalProfileId != profileId)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotFound, "The focus session does not exist.");
                if (endingSession.State is SessionState.Blocked or SessionState.SystemPaused)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotActive,
                        "A paused or blocked session cannot be ended from a stale Desktop timer view.");
                if (endingSession is { State: SessionState.Active } && await IsSessionIdleAsync(repos, req.SessionId, cancellationToken))
                    req = req with { ActiveStudySeconds = endingSession.ActiveStudySeconds };

                var reason = Enum.TryParse<FocusEndReason>(req.EndReason, true, out var r) ? r : FocusEndReason.UserEnded;
                var handler = new EndFocusEarlyHandler(repos.FocusSessions, _clock, recover);
                var result = await handler.HandleAsync(
                    req.SessionId,
                    req.ElapsedSeconds,
                    req.ActiveStudySeconds,
                    req.IdleSeconds,
                    req.ProtectionGapSeconds,
                    reason,
                    cancellationToken);

                var outcome = new SessionOutcomeDto(
                    result.Success,
                    result.Error?.Code,
                    SanitizeErrorMessage(result.Error?.Message),
                    true,
                    result.XpAwarded,
                    result.TotalXpAfter,
                    result.LevelBefore,
                    result.LevelAfter,
                    result.IsStreakQualified,
                    result.AchievementsUnlocked);

                if (!result.Success)
                {
                    return IpcResponse.Fail(
                        requestId,
                        correlationId,
                        MapErrorCode(result.Error?.Code),
                        SanitizeErrorMessage(result.Error?.Message ?? "Failed to end focus session."),
                        JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
                }

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.CompleteFocusSessionOperation:
            {
                var req = DeserializePayload<CompleteFocusSessionRequest>(request.PayloadJson);
                if (req is null || req.SessionId == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "SessionId is required.");
                }

                var endingSession = await repos.FocusSessions.FindAsync(req.SessionId, cancellationToken);
                if (endingSession is null || endingSession.LocalProfileId != profileId)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotFound, "The focus session does not exist.");
                if (endingSession.State is SessionState.Blocked or SessionState.SystemPaused)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotActive,
                        "A paused or blocked session cannot be completed from a stale Desktop timer view.");
                if (endingSession is { State: SessionState.Active } && await IsSessionIdleAsync(repos, req.SessionId, cancellationToken))
                    req = req with { ActiveStudySeconds = endingSession.ActiveStudySeconds };

                var handler = new CompleteFocusSessionHandler(repos.FocusSessions, _clock, recover);
                var result = await handler.HandleAsync(
                    req.SessionId,
                    req.ElapsedSeconds,
                    req.ActiveStudySeconds,
                    req.IdleSeconds,
                    req.ProtectionGapSeconds,
                    cancellationToken);

                var outcome = new SessionOutcomeDto(
                    result.Success,
                    result.Error?.Code,
                    SanitizeErrorMessage(result.Error?.Message),
                    true,
                    result.XpAwarded,
                    result.TotalXpAfter,
                    result.LevelBefore,
                    result.LevelAfter,
                    result.IsStreakQualified,
                    result.AchievementsUnlocked);

                if (!result.Success)
                {
                    return IpcResponse.Fail(
                        requestId,
                        correlationId,
                        MapErrorCode(result.Error?.Code),
                        SanitizeErrorMessage(result.Error?.Message ?? "Failed to complete focus session."),
                        JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
                }

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(outcome, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.ListSessionHistoryOperation:
            {
                int maxCount = 50;
                if (!string.IsNullOrWhiteSpace(request.PayloadJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(request.PayloadJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt32(out var num))
                        {
                            maxCount = num;
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("maxCount", out var p) && p.TryGetInt32(out var count) && count > 0)
                        {
                            maxCount = count;
                        }
                    }
                    catch { }
                }

                var handler = new ListFocusSessionHistoryHandler(repos.LocalProfiles, repos.FocusSessions, repos.RewardLedger);
                var list = await handler.HandleAsync(profileId, maxCount, cancellationToken);
                var dtos = list.Select(s => new FocusSessionDto(
                    s.Id,
                    s.ProfileCode,
                    s.PlannedFocusSeconds,
                    s.ElapsedSeconds,
                    s.ActiveStudySeconds,
                    s.State.ToString(),
                    s.StartedAtUtc,
                    s.EndedAtUtc,
                    s.TaskTitleSnapshot,
                    s.SubjectNameSnapshot,
                    s.XpEarned,
                    s.IsInterrupted)).ToList();

                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dtos, IpcFrameCodec.SerializerOptions));
            }

            // --- Recovery ---
            case IpcProtocol.RecoveryGetStateOperation:
            {
                var coordinator = CreateRecoveryCoordinator(repos, recover, abandon);
                var pending = await coordinator.GetPendingAsync(profileId, cancellationToken);
                var state = new RecoveryState(profileId, MapToRecoveryPendingSession(pending));
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(state, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.RecoveryResumeOperation:
            {
                var sessionId = ExtractSessionId(request.PayloadJson);
                if (!sessionId.HasValue || sessionId.Value == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "SessionId is required for recovery resume.");
                }

                var coordinator = CreateRecoveryCoordinator(repos, recover, abandon);
                var outcome = await coordinator.ResumeAsync(profileId, sessionId.Value, cancellationToken);
                if (!outcome.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(outcome.Error?.Code), SanitizeErrorMessage(outcome.Error?.Message ?? "Failed to resume session."));
                }

                var pending = await coordinator.GetPendingAsync(profileId, cancellationToken);
                var state = new RecoveryState(profileId, MapToRecoveryPendingSession(pending));
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(state, IpcFrameCodec.SerializerOptions));
            }

            case IpcProtocol.RecoveryDiscardOperation:
            {
                var sessionId = ExtractSessionId(request.PayloadJson);
                if (!sessionId.HasValue || sessionId.Value == Guid.Empty)
                {
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "SessionId is required for recovery discard.");
                }

                var coordinator = CreateRecoveryCoordinator(repos, recover, abandon);
                var outcome = await coordinator.DiscardAsync(profileId, sessionId.Value, FocusEndReason.Unknown, cancellationToken);
                if (!outcome.Success)
                {
                    return IpcResponse.Fail(requestId, correlationId, MapErrorCode(outcome.Error?.Code), SanitizeErrorMessage(outcome.Error?.Message ?? "Failed to discard session."));
                }

                var pending = await coordinator.GetPendingAsync(profileId, cancellationToken);
                var state = new RecoveryState(profileId, MapToRecoveryPendingSession(pending));
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(state, IpcFrameCodec.SerializerOptions));
            }

            default:
                return IpcResponse.Fail(
                    requestId,
                    correlationId,
                    IpcErrors.InvalidRequest,
                    $"Unknown operation '{request.Operation}'.");
        }
    }

    private async Task<IpcResponse?> TryHandleBlockingOperationAsync(
        IpcRequest request,
        string requestId,
        string correlationId,
        AgentClientRole clientRole,
        SqliteRepositorySet repos,
        Guid profileId,
        SessionEventRecorder recorder,
        CancellationToken cancellationToken)
    {
        var json = IpcFrameCodec.SerializerOptions;
        switch (request.Operation)
        {
            case IpcProtocol.GetApplicationInterventionOperation:
            {
                var snapshot = _applicationBlockingCoordinator?.GetForDesktop()
                    ?? new ApplicationInterventionDto("Failed", null, null, null,
                        "The Agent application guard is unavailable.", "Unavailable", Array.Empty<string>(), _clock.UtcNow);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(snapshot, json));
            }
            case IpcProtocol.ApplicationOverlayAckOperation:
            {
                var ack = DeserializePayload<ApplicationOverlayAckRequest>(request.PayloadJson);
                if (ack is null || ack.FocusSessionId == Guid.Empty || string.IsNullOrWhiteSpace(ack.TargetKey)
                    || ack.TargetKey.Length > 80 || ack.ErrorCode?.Length > 80)
                    return Invalid("The application overlay acknowledgement is invalid.");
                var accepted = _applicationBlockingCoordinator is not null
                    && await _applicationBlockingCoordinator.AcknowledgeAsync(repos, ack, cancellationToken);
                return accepted
                    ? IpcResponse.Ok(requestId, correlationId)
                    : IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotActive,
                        "The application intervention is no longer current or its target could not be verified.");
            }
            case IpcProtocol.BridgeHelloOperation:
            {
                if (clientRole != AgentClientRole.BrowserBridge) return Unauthorized();
                var hello = DeserializePayload<BridgeHelloRequest>(request.PayloadJson);
                if (hello is null || hello.ProtocolVersion != IpcProtocol.ProtocolVersion
                    || !string.Equals(hello.ExtensionId, NativeMessagingProtocol.ExtensionId, StringComparison.Ordinal)
                    || !TryNormalizeBrowserFamily(hello.BrowserFamily, out var family)
                    || string.IsNullOrWhiteSpace(hello.Version) || hello.Version.Length > 32)
                    return Invalid("The browser extension handshake is invalid or unsupported.");

                _browserBridges[family] = new BrowserBridgeState(family, Environment.TickCount64, _clock.UtcNow, false, null, null);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new { browserFamily = family }, json));
            }
            case IpcProtocol.BridgeHeartbeatOperation:
            {
                if (clientRole != AgentClientRole.BrowserBridge) return Unauthorized();
                var heartbeat = DeserializePayload<BridgeHeartbeatRequest>(request.PayloadJson);
                if (heartbeat is null || !string.Equals(heartbeat.ExtensionId, NativeMessagingProtocol.ExtensionId, StringComparison.Ordinal)
                    || !TryNormalizeBrowserFamily(heartbeat.BrowserFamily, out var family))
                    return Invalid("The browser heartbeat is invalid.");
                var session = await repos.FocusSessions.FindActiveByProfileAsync(profileId, cancellationToken);
                var currentSessionId = session is { IsActive: true } ? session.Id : (Guid?)null;
                _browserBridges.AddOrUpdate(family,
                    _ => new BrowserBridgeState(family, Environment.TickCount64, _clock.UtcNow, false, null, null),
                    (_, prior) => prior with { LastSeenTick = Environment.TickCount64, LastSeenUtc = _clock.UtcNow });
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new BridgeHeartbeatResponse(currentSessionId), json));
            }
            case IpcProtocol.BridgeGetPolicyOperation:
            {
                if (clientRole != AgentClientRole.BrowserBridge) return Unauthorized();
                var session = await repos.FocusSessions.FindActiveByProfileAsync(profileId, cancellationToken);
                var active = session is { IsActive: true }
                    && !string.Equals(session.ProfileCode, BuiltInProfiles.FlexibleCode, StringComparison.OrdinalIgnoreCase);
                var blocked = active
                    ? (await repos.BlockedSites.ListAsync(profileId, cancellationToken)).Where(x => x.IsEnabled).Select(x => x.NormalizedDomain).ToArray()
                    : Array.Empty<string>();
                var allowed = active
                    ? (await repos.AllowedSites.ListAsync(profileId, cancellationToken)).Where(x => x.IsEnabled).Select(x => x.NormalizedDomain).ToArray()
                    : Array.Empty<string>();
                if (active)
                {
                    var now = _clock.UtcNow;
                    var exceptions = await repos.SessionExceptions.ListBySessionAsync(session!.Id, cancellationToken);
                    allowed = allowed.Concat(exceptions
                            .Where(x => x.TargetType == ExceptionTargetType.Website
                                && !x.WasAutoExpired && now >= x.GrantedAtUtc && now < x.ExpiresAtUtc)
                            .Select(x => x.TargetId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                var policy = new BrowserPolicyDto(
                    active ? session!.Id : null,
                    active,
                    active ? _clock.UtcNow.AddSeconds(30) : null,
                    blocked,
                    allowed);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(policy, json));
            }
            case IpcProtocol.BridgePolicyAppliedOperation:
            {
                if (clientRole != AgentClientRole.BrowserBridge) return Unauthorized();
                var applied = DeserializePayload<BridgePolicyAppliedRequest>(request.PayloadJson);
                if (applied is null || !string.Equals(applied.ExtensionId, NativeMessagingProtocol.ExtensionId, StringComparison.Ordinal)
                    || !TryNormalizeBrowserFamily(applied.BrowserFamily, out var family)
                    || applied.ReportedAtUtc.Kind != DateTimeKind.Utc
                    || applied.ErrorCode?.Length > 80)
                    return Invalid("The browser policy status is invalid.");
                _browserBridges.AddOrUpdate(family,
                    _ => new BrowserBridgeState(family, Environment.TickCount64, _clock.UtcNow, applied.IsApplied, applied.IsApplied ? applied.FocusSessionId : null, applied.ErrorCode),
                    (_, prior) => prior with { LastSeenTick = Environment.TickCount64, LastSeenUtc = _clock.UtcNow, IsPolicyApplied = applied.IsApplied, AppliedSessionId = applied.IsApplied ? applied.FocusSessionId : null, LastErrorCode = applied.ErrorCode });
                return IpcResponse.Ok(requestId, correlationId);
            }
            case IpcProtocol.BridgeReportViolationOperation:
            {
                if (clientRole != AgentClientRole.BrowserBridge) return Unauthorized();
                var violation = DeserializePayload<BridgeViolationReportRequest>(request.PayloadJson);
                if (violation is null || violation.FocusSessionId == Guid.Empty
                    || violation.OccurredAtUtc.Kind != DateTimeKind.Utc)
                    return Invalid("The browser violation report is invalid.");
                string domain;
                try { domain = BoundlessStudy.Domain.ValueObjects.NormalizedDomain.Create(violation.BlockedDomain).Value; }
                catch (DomainException) { return Invalid("The reported website is invalid."); }
                var session = await repos.FocusSessions.FindAsync(violation.FocusSessionId, cancellationToken);
                if (session is null || session.LocalProfileId != profileId || !session.IsActive)
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.SessionNotActive, "The reported focus session is not active.");
                if (!IsBrowserProtectionReady(session.Id))
                    return IpcResponse.Fail(requestId, correlationId, "BROWSER_PROTECTION_UNAVAILABLE", "The active browser policy is not verified.");
                var blockedSites = await repos.BlockedSites.ListAsync(profileId, cancellationToken);
                var allowedSites = await repos.AllowedSites.ListAsync(profileId, cancellationToken);
                var exceptions = await repos.SessionExceptions.ListBySessionAsync(session.Id, cancellationToken);
                if (!blockedSites.Any(x => x.IsEnabled && DomainMatches(domain, x.NormalizedDomain))
                    || allowedSites.Any(x => x.IsEnabled && DomainMatches(domain, x.NormalizedDomain))
                    || exceptions.Any(x => x.TargetType == ExceptionTargetType.Website && !x.WasAutoExpired
                        && _clock.UtcNow >= x.GrantedAtUtc && _clock.UtcNow < x.ExpiresAtUtc
                        && DomainMatches(domain, x.TargetId)))
                    return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "The reported domain is not blocked by the active policy.");
                var key = session.Id.ToString("N") + ":" + domain;
                var tick = Environment.TickCount64;
                if (_lastWebsiteViolationTick.TryGetValue(key, out var previous) && tick - previous < 3000)
                    return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new { duplicate = true }, json));
                _lastWebsiteViolationTick[key] = tick;
                if (_lastWebsiteViolationTick.Count > 2048)
                {
                    foreach (var item in _lastWebsiteViolationTick)
                        if (tick - item.Value > 30_000) _lastWebsiteViolationTick.TryRemove(item.Key, out _);
                }
                var handler = new RecordViolationHandler(repos.FocusSessions, repos.UnitOfWork, _clock, recorder);
                var result = await handler.HandleAsync(session.Id, SessionEventType.Blocked, domain, "Website",
                    JsonSerializer.Serialize(new { target = domain, targetType = "Website", attemptedAtUtc = violation.OccurredAtUtc.ToString("o"), intervention = "BrowserRule" }, json), cancellationToken);
                return result.Success
                    ? IpcResponse.Ok(requestId, correlationId)
                    : IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message));
            }
            case IpcProtocol.ListBlockedAppsOperation:
            {
                var rows = await repos.BlockedApps.ListAsync(profileId, cancellationToken);
                var payload = rows.Select(a => new BlockedAppDto(a.Id, a.DisplayName, a.NormalizedExecutablePath, a.NormalizedExecutableName, a.IsEnabled, a.CreatedAtUtc)).ToArray();
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(payload, json));
            }
            case IpcProtocol.AddBlockedAppOperation:
            {
                var req = DeserializePayload<AddBlockedAppRequest>(request.PayloadJson);
                if (req is null) return Invalid("A blocked application target is required.");
                var handler = new AddBlockedAppHandler(repos.BlockedApps, repos.LocalProfiles, repos.UnitOfWork, _clock);
                var result = await handler.HandleAsync(profileId, req.DisplayName, req.ExecutablePath, req.ExecutableName, cancellationToken);
                if (!result.Success) return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message));
                var row = await repos.BlockedApps.FindAsync(result.Id!.Value, cancellationToken);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new BlockedAppDto(row!.Id, row.DisplayName, row.NormalizedExecutablePath, row.NormalizedExecutableName, row.IsEnabled, row.CreatedAtUtc), json));
            }
            case IpcProtocol.RemoveBlockedAppOperation:
            {
                var req = DeserializePayload<RemoveBlockedAppRequest>(request.PayloadJson);
                if (req is null || req.Id == Guid.Empty) return Invalid("A blocked application ID is required.");
                var row = await repos.BlockedApps.FindAsync(req.Id, cancellationToken);
                if (row is null || row.LocalProfileId != profileId) return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "The blocked application was not found.");
                await CommandBoundary.ExecuteAsync(repos.UnitOfWork, async ct =>
                {
                    await repos.BlockedApps.RemoveAsync(row, ct);
                    return UseCaseResult.Ok();
                }, cancellationToken);
                return IpcResponse.Ok(requestId, correlationId);
            }
            case IpcProtocol.ListBlockedSitesOperation:
            case IpcProtocol.ListAllowedSitesOperation:
            {
                if (request.Operation == IpcProtocol.ListBlockedSitesOperation)
                {
                    var rows = await repos.BlockedSites.ListAsync(profileId, cancellationToken);
                    return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(rows.Select(s => new BlockedSiteDto(s.Id, s.NormalizedDomain, s.DisplayName, s.IsEnabled, s.CreatedAtUtc)), json));
                }
                var allowedRows = await repos.AllowedSites.ListAsync(profileId, cancellationToken);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(allowedRows.Select(s => new AllowedSiteDto(s.Id, s.NormalizedDomain, s.DisplayName, s.IsEnabled, s.CreatedAtUtc)), json));
            }
            case IpcProtocol.AddBlockedSiteOperation:
            case IpcProtocol.AddAllowedSiteOperation:
            {
                var allowed = request.Operation == IpcProtocol.AddAllowedSiteOperation;
                if (allowed)
                {
                    var req = DeserializePayload<AddAllowedSiteRequest>(request.PayloadJson);
                    if (req is null) return Invalid("A domain is required.");
                    var handler = new AddAllowedSiteHandler(repos.AllowedSites, repos.LocalProfiles, repos.UnitOfWork, _clock);
                    var result = await handler.HandleAsync(profileId, req.Domain, req.DisplayName, cancellationToken);
                    if (!result.Success) return IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message));
                    var row = await repos.AllowedSites.FindAsync(result.Id!.Value, cancellationToken);
                    return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new AllowedSiteDto(row!.Id, row.NormalizedDomain, row.DisplayName, row.IsEnabled, row.CreatedAtUtc), json));
                }
                var blockedReq = DeserializePayload<AddBlockedSiteRequest>(request.PayloadJson);
                if (blockedReq is null) return Invalid("A domain is required.");
                var addHandler = new AddBlockedSiteHandler(repos.BlockedSites, repos.LocalProfiles, repos.UnitOfWork, _clock);
                var added = await addHandler.HandleAsync(profileId, blockedReq.Domain, blockedReq.DisplayName, cancellationToken);
                if (!added.Success) return IpcResponse.Fail(requestId, correlationId, MapErrorCode(added.Error?.Code), SanitizeErrorMessage(added.Error?.Message));
                var created = await repos.BlockedSites.FindAsync(added.Id!.Value, cancellationToken);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new BlockedSiteDto(created!.Id, created.NormalizedDomain, created.DisplayName, created.IsEnabled, created.CreatedAtUtc), json));
            }
            case IpcProtocol.RemoveBlockedSiteOperation:
            case IpcProtocol.RemoveAllowedSiteOperation:
            {
                if (request.Operation == IpcProtocol.RemoveAllowedSiteOperation)
                {
                    var req = DeserializePayload<RemoveAllowedSiteRequest>(request.PayloadJson);
                    if (req is null || req.Id == Guid.Empty) return Invalid("An allowed-site ID is required.");
                    var row = await repos.AllowedSites.FindAsync(req.Id, cancellationToken);
                    if (row is null || row.LocalProfileId != profileId) return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "The allowed site was not found.");
                    await CommandBoundary.ExecuteAsync(repos.UnitOfWork, async ct =>
                    {
                        await repos.AllowedSites.RemoveAsync(row, ct);
                        return UseCaseResult.Ok();
                    }, cancellationToken);
                    return IpcResponse.Ok(requestId, correlationId);
                }
                var blockedReq = DeserializePayload<RemoveBlockedSiteRequest>(request.PayloadJson);
                if (blockedReq is null || blockedReq.Id == Guid.Empty) return Invalid("A blocked-site ID is required.");
                var blockedRow = await repos.BlockedSites.FindAsync(blockedReq.Id, cancellationToken);
                if (blockedRow is null || blockedRow.LocalProfileId != profileId) return IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, "The blocked site was not found.");
                await CommandBoundary.ExecuteAsync(repos.UnitOfWork, async ct =>
                {
                    await repos.BlockedSites.RemoveAsync(blockedRow, ct);
                    return UseCaseResult.Ok();
                }, cancellationToken);
                return IpcResponse.Ok(requestId, correlationId);
            }
            case IpcProtocol.GetProtectionStatusOperation:
            {
                var apps = await repos.BlockedApps.CountEnabledAsync(profileId, cancellationToken);
                var blocked = await repos.BlockedSites.CountEnabledAsync(profileId, cancellationToken);
                var allowed = (await repos.AllowedSites.ListAsync(profileId, cancellationToken)).Count(x => x.IsEnabled);
                var session = await repos.FocusSessions.FindActiveByProfileAsync(profileId, cancellationToken);
                var browserOk = IsBrowserProtectionReady(session?.Id);
                var flexible = string.Equals(session?.ProfileCode, BuiltInProfiles.FlexibleCode, StringComparison.OrdinalIgnoreCase);
                var appGuard = _applicationBlockingCoordinator?.GetSnapshot();
                var appActive = session is { IsActive: true }
                    && (apps == 0 || flexible || _applicationBlockingCoordinator?.IsProtectionReady(session.Id) == true);
                var configured = apps + blocked > 0;
                var status = !configured ? "NotConfigured" : appActive && (blocked == 0 || flexible || browserOk) ? "Protected" : "Unavailable";
                var recent = _browserBridges.Values.Where(IsFresh).OrderByDescending(b => b.LastSeenUtc).FirstOrDefault();
                var dto = new ProtectionStatusDto(status, appActive, browserOk, apps, blocked, allowed, recent?.LastSeenUtc,
                    status == "Protected" ? null : apps > 0 && !appActive
                        ? appGuard is { State: "Detected" or "Failed" } ? appGuard.EnforcementStatus : "Application overlay enforcement is unavailable."
                        : blocked > 0 && !browserOk ? "Browser rules are not verified as applied in Chrome or Edge." : null);
                return IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(dto, json));
            }
            case IpcProtocol.GrantExceptionOperation:
            {
                var req = DeserializePayload<GrantExceptionRequest>(request.PayloadJson);
                if (req is null || req.FocusSessionId == Guid.Empty
                    || !Enum.TryParse<ExceptionTargetType>(req.TargetType, true, out var targetType)
                    || !Enum.TryParse<ExceptionReasonCategory>(req.ReasonCategory, true, out var reason))
                    return Invalid("The temporary exception request is invalid.");
                string target = req.TargetId?.Trim() ?? string.Empty;
                try
                {
                    target = targetType == ExceptionTargetType.Website
                        ? BoundlessStudy.Domain.ValueObjects.NormalizedDomain.Create(target).Value
                        : BoundlessStudy.Domain.ValueObjects.ExecutableTarget.Create(null, target).NormalizedExecutableName;
                }
                catch (DomainException ex) { return Invalid(SanitizeErrorMessage(ex.Message)); }
                var handler = new GrantSessionExceptionHandler(repos.FocusSessions, repos.FocusProfiles, repos.SessionExceptions, repos.UnitOfWork, _clock, recorder);
                var result = await handler.HandleAsync(req.FocusSessionId, targetType, target, reason, cancellationToken);
                return result.Success ? IpcResponse.Ok(requestId, correlationId, JsonSerializer.Serialize(new { id = result.Id }, json))
                    : IpcResponse.Fail(requestId, correlationId, MapErrorCode(result.Error?.Code), SanitizeErrorMessage(result.Error?.Message));
            }
            case IpcProtocol.RecordViolationOperation:
                return Invalid("Violation events are accepted only from the authenticated browser bridge or enforcement engine.");
            default:
                return null;
        }

        IpcResponse Invalid(string message) => IpcResponse.Fail(requestId, correlationId, IpcErrors.InvalidRequest, message);
        IpcResponse Unauthorized() => IpcResponse.Fail(requestId, correlationId, IpcErrors.UnauthorizedClient, "This operation is restricted to the browser bridge.");
    }

    internal bool IsBrowserProtectionReady(Guid? sessionId) => _browserBridges.Values.Any(state =>
        IsFresh(state) && state.IsPolicyApplied && state.AppliedSessionId == sessionId);

    private static bool IsFresh(BrowserBridgeState state) => Environment.TickCount64 - state.LastSeenTick <= 30_000;

    private static bool TryNormalizeBrowserFamily(string? value, out string family)
    {
        family = (value ?? string.Empty).Trim().ToLowerInvariant();
        return family is "chrome" or "edge";
    }

    private static bool DomainMatches(string host, string rule) =>
        string.Equals(host, rule, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsSessionIdleAsync(
        SqliteRepositorySet repos,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var events = await repos.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId, cancellationToken);
        return events.LastOrDefault(e => e.EventType is SessionEventType.IdleStarted or SessionEventType.IdleEnded)?.EventType
            == SessionEventType.IdleStarted;
    }

    private sealed record BrowserBridgeState(
        string BrowserFamily,
        long LastSeenTick,
        DateTime LastSeenUtc,
        bool IsPolicyApplied,
        Guid? AppliedSessionId,
        string? LastErrorCode);

    private FocusRecoveryCoordinator CreateRecoveryCoordinator(
        SqliteRepositorySet repos,
        RecoverFocusSessionHandler recover,
        AbandonFocusSessionHandler abandon)
    {
        var pending = new GetPendingFocusSessionHandler(repos.LocalProfiles, repos.FocusSessions, _clock);
        var resume = new ResumeFocusRecoveryHandler(repos.LocalProfiles, repos.FocusSessions, recover);
        var discard = new DiscardFocusRecoveryHandler(repos.LocalProfiles, repos.FocusSessions, abandon);
        return new FocusRecoveryCoordinator(repos.LocalProfiles, repos.FocusSessions, _clock, pending, resume, discard);
    }

    private static RecoveryPendingSession? MapToRecoveryPendingSession(PendingFocusSessionInfo? info)
    {
        if (info is null) return null;
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

    private static Guid? ExtractSessionId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if ((doc.RootElement.TryGetProperty("sessionId", out var p) || doc.RootElement.TryGetProperty("SessionId", out p)))
                {
                    if (p.TryGetGuid(out var gid)) return gid;
                    if (Guid.TryParse(p.GetString(), out var parsed)) return parsed;
                }
            }
        }
        catch { }
        return null;
    }

    private static T? DeserializePayload<T>(string? payloadJson) where T : class
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        return JsonSerializer.Deserialize<T>(payloadJson, IpcFrameCodec.SerializerOptions);
    }

    private static string MapErrorCode(string? code) => code switch
    {
        UseCaseErrorCodes.ProfileNotFound => IpcErrors.ProfileNotFound,
        UseCaseErrorCodes.SessionNotFound => IpcErrors.SessionNotFound,
        UseCaseErrorCodes.TaskNotFound => IpcErrors.TaskNotFound,
        UseCaseErrorCodes.SubjectNotFound => IpcErrors.SubjectNotFound,
        UseCaseErrorCodes.RecoverableSessionPending => IpcErrors.RecoverableSessionPending,
        _ => string.IsNullOrWhiteSpace(code) ? IpcErrors.OperationFailed : code
    };

    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Operation failed.";
        }

        // 1. Remove file paths (e.g. C:\... or E:\...)
        var sanitized = Regex.Replace(message, @"[A-Za-z]:\\[^ \t\r\n\(\)]+", "[redacted-path]");

        // 2. Remove stack traces
        if (sanitized.Contains("   at "))
        {
            sanitized = sanitized.Split(new[] { "   at " }, StringSplitOptions.None)[0].Trim();
        }
        if (sanitized.Contains("--- End of stack trace"))
        {
            sanitized = sanitized.Split(new[] { "--- End of stack trace" }, StringSplitOptions.None)[0].Trim();
        }

        return sanitized;
    }

    private void PruneIdempotencyCache()
    {
        var cutoff = _clock.UtcNow - _idempotencyTtl;
        foreach (var kvp in _idempotencyCache)
        {
            if (kvp.Value.CachedAtUtc < cutoff)
            {
                _idempotencyCache.TryRemove(kvp.Key, out _);
            }
        }
    }

}
