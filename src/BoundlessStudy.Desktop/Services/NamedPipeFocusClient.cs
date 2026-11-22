using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Domain;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// IPC focus client connecting to the agent's Named Pipe interface (system-architecture.md §7.2; ADR-0005).
/// Delegates all operations to <see cref="IAgentClient"/> (<see cref="StudyAgentClient"/>), ensuring
/// unified connection management, SID authorization, framing, concurrency gating, and error propagation.
/// Desktop never touches SQLite or Infrastructure directly.
/// </summary>
public sealed class NamedPipeFocusClient : IFocusClient, IAsyncDisposable, IDisposable
{
    private readonly IAgentClient _agentClient;
    private readonly bool _ownsClient;

    public NamedPipeFocusClient(IAgentClient agentClient)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _ownsClient = false;
    }

    public NamedPipeFocusClient(string? pipeName = null, int connectTimeoutMs = 1500)
    {
        _agentClient = new StudyAgentClient(pipeName, connectTimeoutMs);
        _ownsClient = true;
    }

    public IAgentClient AgentClient => _agentClient;

    public async Task<bool> PingAgentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _agentClient.PingAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    public async Task<ProfileProgress?> GetProfileProgressAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await _agentClient.GetProfileProgressAsync(cancellationToken);
            if (dto is null) return null;

            return new ProfileProgress(
                dto.TotalXp,
                dto.Level,
                dto.CurrentStreakDays,
                dto.LongestStreakDays,
                dto.QualifiedSessionCount,
                dto.StrictQualifiedSessionCount,
                dto.CompletedTaskCount,
                dto.TodayLocal,
                dto.SessionXpToday,
                dto.SessionCapRemainingToday);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FocusProfileSummary>> ListFocusProfilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await _agentClient.ListFocusProfilesAsync(cancellationToken);
            return dtos.Select(d => new FocusProfileSummary(
                d.Code,
                d.DisplayName,
                d.EarlyExitMode,
                d.XpPerActiveMinute,
                d.MaxExceptionsPerSession,
                d.IsEnabled)).ToList();
        }
        catch
        {
            return Array.Empty<FocusProfileSummary>();
        }
    }

    public async Task<FocusSessionSummary?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dto = await _agentClient.GetActiveFocusSessionAsync(cancellationToken);
            if (dto is null) return null;

            var state = Enum.TryParse<SessionState>(dto.State, true, out var parsedState) ? parsedState : SessionState.Active;
            return new FocusSessionSummary(
                dto.Id,
                dto.ProfileCode,
                dto.PlannedFocusSeconds,
                dto.ElapsedSeconds,
                dto.ActiveStudySeconds,
                state,
                dto.StartedAtUtc,
                dto.EndedAtUtc,
                dto.TaskTitleSnapshot,
                dto.SubjectNameSnapshot,
                dto.XpEarned,
                dto.IsInterrupted,
                dto.IsIdle,
                dto.IdleDurationSeconds);
        }
        catch
        {
            return null;
        }
    }

    public async Task ApplyCheckpointAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        CancellationToken cancellationToken = default)
    {
        await _agentClient.ApplyFocusCheckpointAsync(
            new ApplyFocusCheckpointRequest(sessionId, elapsedSeconds, activeStudySeconds, 0, 0, 0, 0, 0),
            cancellationToken);
    }

    public async Task<EntityResult> StartFocusSessionAsync(
        string profileCode,
        int plannedFocusSeconds,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var id = await _agentClient.StartFocusSessionAsync(
                new StartFocusSessionRequest(profileCode, plannedFocusSeconds, taskId),
                cancellationToken);
            return EntityResult.Ok(id);
        }
        catch (AgentException ex)
        {
            return EntityResult.Fail(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            return EntityResult.Fail(IpcErrors.AgentUnavailable, ex.Message);
        }
    }

    public async Task<SessionCompletionOutcome> CompleteFocusSessionAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds = 0,
        int protectionGapSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new CompleteFocusSessionRequest(sessionId, elapsedSeconds, activeStudySeconds, idleSeconds, protectionGapSeconds);
            var dto = await _agentClient.CompleteFocusSessionAsync(req, cancellationToken);
            return new SessionCompletionOutcome(
                dto.Success,
                dto.ErrorCode is null ? null : new UseCaseError(dto.ErrorCode, dto.ErrorMessage ?? string.Empty),
                true,
                dto.AwardXp,
                dto.TotalXp,
                dto.LevelBefore,
                dto.LevelAfter,
                dto.StreakQualified,
                dto.UnlockedAchievements);
        }
        catch (AgentException ex)
        {
            return new SessionCompletionOutcome(
                false,
                new UseCaseError(ex.ErrorCode, ex.Message),
                false,
                0, 0, 0, 0, false, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new SessionCompletionOutcome(
                false,
                new UseCaseError(IpcErrors.AgentUnavailable, ex.Message),
                false,
                0, 0, 0, 0, false, Array.Empty<string>());
        }
    }

    public async Task<SessionCompletionOutcome> EndFocusEarlyAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        FocusEndReason reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var req = new EndFocusEarlyRequest(sessionId, elapsedSeconds, activeStudySeconds, idleSeconds, protectionGapSeconds, reason.ToString());
            var dto = await _agentClient.EndFocusEarlyAsync(req, cancellationToken);
            return new SessionCompletionOutcome(
                dto.Success,
                dto.ErrorCode is null ? null : new UseCaseError(dto.ErrorCode, dto.ErrorMessage ?? string.Empty),
                true,
                dto.AwardXp,
                dto.TotalXp,
                dto.LevelBefore,
                dto.LevelAfter,
                dto.StreakQualified,
                dto.UnlockedAchievements);
        }
        catch (AgentException ex)
        {
            return new SessionCompletionOutcome(
                false,
                new UseCaseError(ex.ErrorCode, ex.Message),
                false,
                0, 0, 0, 0, false, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new SessionCompletionOutcome(
                false,
                new UseCaseError(IpcErrors.AgentUnavailable, ex.Message),
                false,
                0, 0, 0, 0, false, Array.Empty<string>());
        }
    }

    public async Task<IReadOnlyList<FocusSessionSummary>> ListSessionHistoryAsync(
        int maxCount = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await _agentClient.ListFocusSessionHistoryAsync(maxCount, cancellationToken);
            return dtos.Select(dto =>
            {
                var state = Enum.TryParse<SessionState>(dto.State, true, out var parsedState) ? parsedState : SessionState.Completed;
                return new FocusSessionSummary(
                    dto.Id,
                    dto.ProfileCode,
                    dto.PlannedFocusSeconds,
                    dto.ElapsedSeconds,
                    dto.ActiveStudySeconds,
                    state,
                    dto.StartedAtUtc,
                    dto.EndedAtUtc,
                    dto.TaskTitleSnapshot,
                    dto.SubjectNameSnapshot,
                    dto.XpEarned,
                    dto.IsInterrupted);
            }).ToList();
        }
        catch
        {
            return Array.Empty<FocusSessionSummary>();
        }
    }

    public async Task<IReadOnlyList<StudyTaskSummary>> ListTasksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await _agentClient.GetTasksAsync(cancellationToken);
            return dtos.Select(d =>
            {
                var status = Enum.TryParse<StudyTaskStatus>(d.Status, true, out var s) ? s : StudyTaskStatus.Todo;
                var priority = Enum.TryParse<TaskPriority>(d.Priority, true, out var p) ? p : TaskPriority.Normal;
                return new StudyTaskSummary(
                    d.Id,
                    d.Title,
                    d.Notes,
                    d.SubjectId,
                    d.SubjectName,
                    status,
                    priority,
                    d.DueDateLocal,
                    d.EstimatedMinutes,
                    d.IsArchived,
                    d.IsDeleted,
                    d.CreatedAtUtc,
                    d.CompletedAtUtc,
                    d.PlannedForLocalDate);
            }).ToList();
        }
        catch
        {
            return Array.Empty<StudyTaskSummary>();
        }
    }

    public async Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var dtos = await _agentClient.GetSubjectsAsync(true, cancellationToken);
            return dtos.Select(d => new SubjectSummary(
                d.Id,
                d.Name,
                d.Description,
                d.IsArchived,
                d.CreatedAtUtc)).ToList();
        }
        catch
        {
            return Array.Empty<SubjectSummary>();
        }
    }

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
