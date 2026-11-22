using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

public sealed record SubjectSummary(
    Guid Id,
    string Name,
    string? Description,
    bool IsArchived,
    DateTime CreatedAtUtc);

public sealed record StudyTaskSummary(
    Guid Id,
    string Title,
    string? Notes,
    Guid? SubjectId,
    string? SubjectName,
    StudyTaskStatus Status,
    TaskPriority Priority,
    DateOnly? DueDateLocal,
    int? EstimatedMinutes,
    bool IsArchived,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    DateOnly? PlannedForLocalDate);

public sealed record FocusProfileSummary(
    string Code,
    string DisplayName,
    string EarlyExitMode,
    int XpPerActiveMinute,
    int MaxExceptionsPerSession,
    bool IsEnabled);

public sealed record FocusSessionSummary(
    Guid Id,
    string ProfileCode,
    int PlannedFocusSeconds,
    int ElapsedSeconds,
    int ActiveStudySeconds,
    SessionState State,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string? TaskTitleSnapshot,
    string? SubjectNameSnapshot,
    int XpEarned,
    bool IsInterrupted,
    bool IsIdle = false,
    int IdleDurationSeconds = 0);

/// <summary>Query handler to list subjects for a profile.</summary>
public sealed class ListSubjectsHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly ISubjectRepository _subjects;

    public ListSubjectsHandler(ILocalProfileRepository profiles, ISubjectRepository subjects)
    {
        _profiles = profiles;
        _subjects = subjects;
    }

    public async Task<IReadOnlyList<SubjectSummary>> HandleAsync(Guid localProfileId, bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return Array.Empty<SubjectSummary>();
        }

        var subjects = await _subjects.ListAsync(localProfileId, includeArchived, cancellationToken);
        return subjects
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SubjectSummary(s.Id, s.Name, s.Description, s.IsArchived, s.CreatedAtUtc))
            .ToList();
    }
}

/// <summary>Query handler to list tasks for a profile, optionally resolving subject names.</summary>
public sealed class ListTasksHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IStudyTaskRepository _tasks;
    private readonly ISubjectRepository _subjects;

    public ListTasksHandler(
        ILocalProfileRepository profiles,
        IStudyTaskRepository tasks,
        ISubjectRepository subjects)
    {
        _profiles = profiles;
        _tasks = tasks;
        _subjects = subjects;
    }

    public async Task<IReadOnlyList<StudyTaskSummary>> HandleAsync(Guid localProfileId, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return Array.Empty<StudyTaskSummary>();
        }

        var tasks = await _tasks.ListAsync(localProfileId, cancellationToken);
        var subjects = (await _subjects.ListAsync(localProfileId, includeArchived: true, cancellationToken))
            .ToDictionary(s => s.Id, s => s.Name);

        return tasks
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t =>
            {
                string? subjectName = t.SubjectId.HasValue && subjects.TryGetValue(t.SubjectId.Value, out var name)
                    ? name
                    : null;

                return new StudyTaskSummary(
                    t.Id,
                    t.Title,
                    t.Notes,
                    t.SubjectId,
                    subjectName,
                    t.Status,
                    t.Priority,
                    t.DueDateLocal,
                    t.EstimatedMinutes,
                    t.Status == StudyTaskStatus.Archived,
                    t.IsDeleted,
                    t.CreatedAtUtc,
                    t.CompletedAtUtc,
                    t.PlannedForLocalDate);
            })
            .ToList();
    }
}

/// <summary>Query handler to list enabled focus profiles.</summary>
public sealed class ListFocusProfilesHandler
{
    private readonly IFocusProfileRepository _focusProfiles;

    public ListFocusProfilesHandler(IFocusProfileRepository focusProfiles)
    {
        _focusProfiles = focusProfiles;
    }

    public async Task<IReadOnlyList<FocusProfileSummary>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _focusProfiles.ListAsync(cancellationToken);
        return profiles
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Code)
            .Select(p => new FocusProfileSummary(
                p.Code,
                p.DisplayName,
                p.EarlyExitMode.ToString(),
                p.XpPerActiveMinute,
                p.MaxExceptionsPerSession,
                p.IsEnabled))
            .ToList();
    }
}

/// <summary>Query handler to get the currently active or recoverable focus session for a profile.</summary>
public sealed class GetActiveFocusSessionHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;

    public GetActiveFocusSessionHandler(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions)
    {
        _profiles = profiles;
        _sessions = sessions;
    }

    public async Task<FocusSessionSummary?> HandleAsync(Guid localProfileId, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return null;
        }

        var session = await _sessions.FindActiveByProfileAsync(localProfileId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        return new FocusSessionSummary(
            session.Id,
            session.ProfileCode,
            session.PlannedFocusSeconds,
            session.ElapsedSeconds,
            session.ActiveStudySeconds,
            session.State,
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.TaskTitleSnapshot,
            session.SubjectNameSnapshot,
            0,
            session.State == SessionState.RecoverableInterrupted);
    }
}

/// <summary>Query handler to list recent focus sessions for history view.</summary>
public sealed class ListFocusSessionHistoryHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IFocusSessionRepository _sessions;
    private readonly IRewardLedgerRepository _ledger;

    public ListFocusSessionHistoryHandler(
        ILocalProfileRepository profiles,
        IFocusSessionRepository sessions,
        IRewardLedgerRepository ledger)
    {
        _profiles = profiles;
        _sessions = sessions;
        _ledger = ledger;
    }

    public async Task<IReadOnlyList<FocusSessionSummary>> HandleAsync(Guid localProfileId, int maxCount = 50, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return Array.Empty<FocusSessionSummary>();
        }

        var sessions = await _sessions.ListByProfileAsync(localProfileId, cancellationToken);
        var entries = await _ledger.ListByProfileAsync(localProfileId, cancellationToken);

        var xpBySessionId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.SourceType is LedgerSourceType.SessionCompletion or LedgerSourceType.SessionPartial)
            {
                xpBySessionId[entry.SourceId] = entry.Amount;
            }
        }

        return sessions
            .OrderByDescending(s => s.StartedAtUtc)
            .Take(maxCount)
            .Select(s =>
            {
                xpBySessionId.TryGetValue(s.Id.ToString(), out var xp);
                return new FocusSessionSummary(
                    s.Id,
                    s.ProfileCode,
                    s.PlannedFocusSeconds,
                    s.ElapsedSeconds,
                    s.ActiveStudySeconds,
                    s.State,
                    s.StartedAtUtc,
                    s.EndedAtUtc,
                    s.TaskTitleSnapshot,
                    s.SubjectNameSnapshot,
                    xp,
                    s.State == SessionState.RecoverableInterrupted);
            })
            .ToList();
    }
}
