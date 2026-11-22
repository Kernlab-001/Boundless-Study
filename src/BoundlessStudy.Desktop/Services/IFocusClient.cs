using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Client interface for interacting with focus sessions, progress, profiles, and history.
/// Follows ADR-0005: desktop UI never references Infrastructure or SQLite directly.
/// </summary>
public interface IFocusClient
{
    Task<bool> PingAgentAsync(CancellationToken cancellationToken = default);

    Task<ProfileProgress?> GetProfileProgressAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FocusProfileSummary>> ListFocusProfilesAsync(CancellationToken cancellationToken = default);

    Task<FocusSessionSummary?> GetActiveSessionAsync(CancellationToken cancellationToken = default);

    Task ApplyCheckpointAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        CancellationToken cancellationToken = default);

    Task<EntityResult> StartFocusSessionAsync(
        string profileCode,
        int plannedFocusSeconds,
        Guid? taskId = null,
        CancellationToken cancellationToken = default);

    Task<SessionCompletionOutcome> CompleteFocusSessionAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds = 0,
        int protectionGapSeconds = 0,
        CancellationToken cancellationToken = default);

    Task<SessionCompletionOutcome> EndFocusEarlyAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        FocusEndReason reason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FocusSessionSummary>> ListSessionHistoryAsync(
        int maxCount = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudyTaskSummary>> ListTasksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken cancellationToken = default);
}
