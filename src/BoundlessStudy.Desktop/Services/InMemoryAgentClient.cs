using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// In-memory implementation of <see cref="IAgentClient"/> for desktop preview, testing,
/// and disconnected development. Enforces reward and workflow invariants without opening SQLite.
/// </summary>
public sealed class InMemoryAgentClient : IAgentClient
{
    private readonly List<SubjectDto> _subjects = new();
    private readonly List<StudyTaskDto> _tasks = new();
    private readonly object _lock = new();

    public InMemoryAgentClient(
        IEnumerable<SubjectDto>? initialSubjects = null,
        IEnumerable<StudyTaskDto>? initialTasks = null)
    {
        if (initialSubjects is not null)
        {
            _subjects.AddRange(initialSubjects);
        }

        if (initialTasks is not null)
        {
            _tasks.AddRange(initialTasks);
        }
    }

    public IReadOnlyList<SubjectDto> AllSubjects
    {
        get
        {
            lock (_lock)
            {
                return _subjects.ToList();
            }
        }
    }

    public IReadOnlyList<StudyTaskDto> AllTasks
    {
        get
        {
            lock (_lock)
            {
                return _tasks.ToList();
            }
        }
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<ProfileProgressDto?> GetProfileProgressAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var completedCount = _tasks.Count(t => t.Status == "Done");
            var dto = new ProfileProgressDto(
                TotalXp: completedCount * 25,
                Level: 1 + (completedCount * 25 / 100),
                CurrentStreakDays: 1,
                LongestStreakDays: 1,
                QualifiedSessionCount: 0,
                StrictQualifiedSessionCount: 0,
                CompletedTaskCount: completedCount,
                TodayLocal: DateOnly.FromDateTime(DateTime.UtcNow),
                SessionXpToday: 0,
                SessionCapRemainingToday: 300,
                DailySessionXpCap: 300);

            return Task.FromResult<ProfileProgressDto?>(dto);
        }
    }

    public Task<ProtectionStatusDto> GetProtectionStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProtectionStatusDto(
            "Unavailable", false, false, 0, 0, 0, null,
            "The in-memory client does not run application or browser enforcement."));

    public Task<ApplicationInterventionDto?> GetApplicationInterventionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ApplicationInterventionDto?>(null);

    public Task AcknowledgeApplicationOverlayAsync(ApplicationOverlayAckRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task GrantSessionExceptionAsync(GrantExceptionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("The in-memory client cannot grant real focus-session exceptions."));

    public Task<IReadOnlyList<SubjectDto>> GetSubjectsAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var results = _subjects
                .Where(s => includeArchived || !s.IsArchived)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<SubjectDto>>(results);
        }
    }

    public Task<SubjectDto> CreateSubjectAsync(CreateSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Subject name is required.", nameof(request));
        }

        if (request.Name.Trim().Length > 100)
        {
            throw new ArgumentException("Subject name cannot exceed 100 characters.", nameof(request));
        }

        lock (_lock)
        {
            var subject = new SubjectDto(
                Guid.NewGuid(),
                request.Name.Trim(),
                request.Description?.Trim(),
                IsArchived: false,
                DateTime.UtcNow);

            _subjects.Add(subject);
            return Task.FromResult(subject);
        }
    }

    public Task<SubjectDto> UpdateSubjectAsync(UpdateSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Subject name is required.", nameof(request));
        }

        if (request.Name.Trim().Length > 100)
        {
            throw new ArgumentException("Subject name cannot exceed 100 characters.", nameof(request));
        }

        lock (_lock)
        {
            var index = _subjects.FindIndex(s => s.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Subject with ID '{request.Id}' was not found.");
            }

            var existing = _subjects[index];
            var updated = existing with
            {
                Name = request.Name.Trim(),
                Description = request.Description?.Trim()
            };

            _subjects[index] = updated;

            // Also update any tasks referencing this subject
            for (int i = 0; i < _tasks.Count; i++)
            {
                if (_tasks[i].SubjectId == request.Id)
                {
                    _tasks[i] = _tasks[i] with { SubjectName = updated.Name };
                }
            }

            return Task.FromResult(updated);
        }
    }

    public Task<SubjectDto> ArchiveSubjectAsync(ArchiveSubjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _subjects.FindIndex(s => s.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Subject with ID '{request.Id}' was not found.");
            }

            var existing = _subjects[index];
            var updated = existing with { IsArchived = !request.Restore };
            _subjects[index] = updated;

            return Task.FromResult(updated);
        }
    }

    public Task<IReadOnlyList<StudyTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var results = _tasks
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.CreatedAtUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<StudyTaskDto>>(results);
        }
    }

    public Task<StudyTaskDto> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(request));
        }

        lock (_lock)
        {
            string? subjectName = null;
            if (request.SubjectId is { } sid)
            {
                var subject = _subjects.FirstOrDefault(s => s.Id == sid);
                if (subject is null)
                {
                    throw new KeyNotFoundException($"Linked subject with ID '{sid}' was not found.");
                }
                subjectName = subject.Name;
            }

            var task = new StudyTaskDto(
                Guid.NewGuid(),
                request.Title.Trim(),
                request.Notes?.Trim(),
                request.SubjectId,
                subjectName,
                Status: "Todo",
                Priority: string.IsNullOrWhiteSpace(request.Priority) ? "Normal" : request.Priority,
                DueDateLocal: request.DueDateLocal,
                EstimatedMinutes: request.EstimatedMinutes,
                IsArchived: false,
                IsDeleted: false,
                CreatedAtUtc: DateTime.UtcNow,
                CompletedAtUtc: null,
                PlannedForLocalDate: request.PlannedForLocalDate);

            _tasks.Add(task);
            return Task.FromResult(task);
        }
    }

    public Task<StudyTaskDto> UpdateTaskAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(request));
        }

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            string? subjectName = null;
            if (request.SubjectId is { } sid)
            {
                var subject = _subjects.FirstOrDefault(s => s.Id == sid);
                if (subject is null)
                {
                    throw new KeyNotFoundException($"Linked subject with ID '{sid}' was not found.");
                }
                subjectName = subject.Name;
            }

            var existing = _tasks[index];
            var updated = existing with
            {
                Title = request.Title.Trim(),
                Notes = request.Notes?.Trim(),
                SubjectId = request.SubjectId,
                SubjectName = subjectName,
                Priority = string.IsNullOrWhiteSpace(request.Priority) ? existing.Priority : request.Priority,
                DueDateLocal = request.DueDateLocal,
                EstimatedMinutes = request.EstimatedMinutes,
                PlannedForLocalDate = request.PlannedForLocalDate
            };

            _tasks[index] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<StudyTaskDto> CompleteTaskAsync(CompleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            var existing = _tasks[index];
            if (existing.IsDeleted)
            {
                throw new InvalidOperationException("A deleted task cannot be completed.");
            }

            // Invariant TV-09 / TV-18: If already completed previously, preserve original CompletedAtUtc timestamp!
            var completedAt = existing.CompletedAtUtc ?? DateTime.UtcNow;

            var updated = existing with
            {
                Status = "Done",
                CompletedAtUtc = completedAt
            };

            _tasks[index] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<StudyTaskDto> ReopenTaskAsync(ReopenTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            var existing = _tasks[index];
            // Invariant §8.5: Reopening preserves original CompletedAtUtc timestamp and historical reward!
            var updated = existing with
            {
                Status = "Todo"
                // CompletedAtUtc remains intact
            };

            _tasks[index] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<StudyTaskDto> StartTaskAsync(StartTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            var existing = _tasks[index];
            var updated = existing with { Status = "InProgress" };
            _tasks[index] = updated;

            return Task.FromResult(updated);
        }
    }

    public Task<StudyTaskDto> ArchiveTaskAsync(ArchiveTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            var existing = _tasks[index];
            var isArchived = !request.Restore;
            var status = isArchived ? "Archived" : (existing.CompletedAtUtc.HasValue ? "Done" : "Todo");

            var updated = existing with
            {
                IsArchived = isArchived,
                Status = status
            };

            _tasks[index] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<StudyTaskDto> DeleteTaskAsync(SoftDeleteTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == request.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Task with ID '{request.Id}' was not found.");
            }

            var existing = _tasks[index];
            // Soft-delete preserves history contribution per §9.2
            var updated = existing with { IsDeleted = true };
            _tasks[index] = updated;

            return Task.FromResult(updated);
        }
    }

    public Task<IReadOnlyList<FocusProfileDto>> ListFocusProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FocusProfileDto>>(new List<FocusProfileDto>
        {
            new("standard", "Standard Focus", "AllowExceptions", 1, 3, true),
            new("deep", "Deep Work", "Strict", 2, 0, true)
        });

    public Task<FocusSessionDto?> GetActiveFocusSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<FocusSessionDto?>(null);

    public Task<Guid> StartFocusSessionAsync(StartFocusSessionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Guid.NewGuid());

    public Task ApplyFocusCheckpointAsync(ApplyFocusCheckpointRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<SessionOutcomeDto> EndFocusEarlyAsync(EndFocusEarlyRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionOutcomeDto(true, null, null, true, 0, 0, 1, 1, false, Array.Empty<string>()));

    public Task<SessionOutcomeDto> CompleteFocusSessionAsync(CompleteFocusSessionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionOutcomeDto(true, null, null, true, 50, 50, 1, 1, true, Array.Empty<string>()));

    public Task<IReadOnlyList<FocusSessionDto>> ListFocusSessionHistoryAsync(int maxCount = 50, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FocusSessionDto>>(Array.Empty<FocusSessionDto>());

    public Task<RecoveryResponse> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RecoveryResponse.Ok(Guid.NewGuid().ToString("D"), new RecoveryState(Guid.NewGuid(), null)));

    public Task<RecoveryResponse> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RecoveryResponse.Ok(Guid.NewGuid().ToString("D"), new RecoveryState(Guid.NewGuid(), null)));

    public Task<RecoveryResponse> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RecoveryResponse.Ok(Guid.NewGuid().ToString("D"), new RecoveryState(Guid.NewGuid(), null)));

    public ValueTask DisposeAsync() =>
        ValueTask.CompletedTask;
}
