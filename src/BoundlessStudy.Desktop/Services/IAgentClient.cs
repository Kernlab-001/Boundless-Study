using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// UI client boundary for communicating with the background agent for study workflows (system-architecture.md §7.2; ADR-0005).
/// Desktop never references BoundlessStudy.Infrastructure or opens SQLite directly.
/// </summary>
public interface IAgentClient : IRecoveryClient, IAsyncDisposable
{
    // Connectivity & Progress
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
    Task<ProfileProgressDto?> GetProfileProgressAsync(CancellationToken cancellationToken = default);
    Task<ProtectionStatusDto> GetProtectionStatusAsync(CancellationToken cancellationToken = default);
    Task<ApplicationInterventionDto?> GetApplicationInterventionAsync(CancellationToken cancellationToken = default);
    Task AcknowledgeApplicationOverlayAsync(ApplicationOverlayAckRequest request, CancellationToken cancellationToken = default);
    Task GrantSessionExceptionAsync(GrantExceptionRequest request, CancellationToken cancellationToken = default);

    // Subjects
    Task<IReadOnlyList<SubjectDto>> GetSubjectsAsync(bool includeArchived = true, CancellationToken cancellationToken = default);
    Task<SubjectDto> CreateSubjectAsync(CreateSubjectRequest request, CancellationToken cancellationToken = default);
    Task<SubjectDto> UpdateSubjectAsync(UpdateSubjectRequest request, CancellationToken cancellationToken = default);
    Task<SubjectDto> ArchiveSubjectAsync(ArchiveSubjectRequest request, CancellationToken cancellationToken = default);

    // Tasks
    Task<IReadOnlyList<StudyTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default);
    Task<StudyTaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> CompleteTaskAsync(CompleteTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> ReopenTaskAsync(ReopenTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> StartTaskAsync(StartTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> ArchiveTaskAsync(ArchiveTaskRequest request, CancellationToken cancellationToken = default);
    Task<StudyTaskDto> DeleteTaskAsync(SoftDeleteTaskRequest request, CancellationToken cancellationToken = default);

    // Focus Profiles & Sessions
    Task<IReadOnlyList<FocusProfileDto>> ListFocusProfilesAsync(CancellationToken cancellationToken = default);
    Task<FocusSessionDto?> GetActiveFocusSessionAsync(CancellationToken cancellationToken = default);
    Task<Guid> StartFocusSessionAsync(StartFocusSessionRequest request, CancellationToken cancellationToken = default);
    Task ApplyFocusCheckpointAsync(ApplyFocusCheckpointRequest request, CancellationToken cancellationToken = default);
    Task<SessionOutcomeDto> EndFocusEarlyAsync(EndFocusEarlyRequest request, CancellationToken cancellationToken = default);
    Task<SessionOutcomeDto> CompleteFocusSessionAsync(CompleteFocusSessionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FocusSessionDto>> ListFocusSessionHistoryAsync(int maxCount = 50, CancellationToken cancellationToken = default);
}
