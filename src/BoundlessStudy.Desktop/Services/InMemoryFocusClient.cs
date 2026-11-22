using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// In-memory implementation of <see cref="IFocusClient"/> for tests, UI design-time preview,
/// and offline/fallback usage without database or network access.
/// </summary>
public sealed class InMemoryFocusClient : IFocusClient
{
    private readonly List<FocusProfileSummary> _profiles =
    [
        new(BuiltInProfiles.FlexibleCode, "Flexible", "Immediate", 2, 0, true),
        new(BuiltInProfiles.BalancedCode, "Balanced", "ConfirmationCooldown", 3, 2, true),
        new(BuiltInProfiles.StrictCode, "Strict", "TypedConfirmationCooldown", 4, 1, true)
    ];

    private readonly List<FocusSessionSummary> _sessions = [];
    private readonly List<StudyTaskSummary> _tasks =
    [
        new(Guid.NewGuid(), "Read Chapter 4: System Architecture", "Review ADRs and pipe protocol", null, "Computer Science", StudyTaskStatus.InProgress, TaskPriority.High, null, 45, false, false, DateTime.UtcNow.AddDays(-1), null, null),
        new(Guid.NewGuid(), "Solve Distributed Systems Exercise 2", "Multi-process concurrency check", null, "Computer Science", StudyTaskStatus.Todo, TaskPriority.Normal, null, 30, false, false, DateTime.UtcNow.AddDays(-1), null, null)
    ];
    private readonly List<SubjectSummary> _subjects =
    [
        new(Guid.NewGuid(), "Computer Science", "Core systems and software engineering", false, DateTime.UtcNow.AddDays(-10)),
        new(Guid.NewGuid(), "Mathematics", "Probability and linear algebra", false, DateTime.UtcNow.AddDays(-10))
    ];

    private ProfileProgress _progress;
    private FocusSessionSummary? _activeSession;
    private bool _pingSucceeds = true;

    public InMemoryFocusClient(ProfileProgress? initialProgress = null)
    {
        _progress = initialProgress ?? new ProfileProgress(
            TotalXp: 0,
            Level: 1,
            CurrentStreakDays: 0,
            LongestStreakDays: 0,
            QualifiedSessionCount: 0,
            StrictQualifiedSessionCount: 0,
            CompletedTaskCount: 0,
            TodayLocal: DateOnly.FromDateTime(DateTime.UtcNow),
            SessionXpToday: 0,
            SessionCapRemainingToday: EconomyRules.DailySessionXpCap);
    }

    public void ConfigurePing(bool succeeds) => _pingSucceeds = succeeds;

    public Task<bool> PingAgentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_pingSucceeds);

    public Task<ProfileProgress?> GetProfileProgressAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ProfileProgress?>(_progress);

    public Task<IReadOnlyList<FocusProfileSummary>> ListFocusProfilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FocusProfileSummary>>(_profiles.AsReadOnly());

    public Task<FocusSessionSummary?> GetActiveSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_activeSession);

    public Task ApplyCheckpointAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        CancellationToken cancellationToken = default)
    {
        if (_activeSession?.Id == sessionId)
        {
            _activeSession = _activeSession with
            {
                ElapsedSeconds = Math.Max(_activeSession.ElapsedSeconds, elapsedSeconds),
                ActiveStudySeconds = Math.Max(_activeSession.ActiveStudySeconds, activeStudySeconds)
            };
        }

        return Task.CompletedTask;
    }

    public Task<EntityResult> StartFocusSessionAsync(
        string profileCode,
        int plannedFocusSeconds,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        if (_activeSession is not null && _activeSession.State is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused)
        {
            return Task.FromResult(EntityResult.Fail(UseCaseErrorCodes.ActiveFocusSessionExists, "A focus session is already active."));
        }

        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Code, profileCode, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !profile.IsEnabled)
        {
            return Task.FromResult(EntityResult.Fail(UseCaseErrorCodes.FocusProfileNotFound, $"Focus profile '{profileCode}' not found."));
        }

        string? taskTitle = null;
        string? subjectName = null;
        if (taskId.HasValue)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == taskId.Value);
            taskTitle = task?.Title;
            subjectName = task?.SubjectName;
        }

        var newSession = new FocusSessionSummary(
            Id: Guid.NewGuid(),
            ProfileCode: profile.Code,
            PlannedFocusSeconds: plannedFocusSeconds,
            ElapsedSeconds: 0,
            ActiveStudySeconds: 0,
            State: SessionState.Active,
            StartedAtUtc: DateTime.UtcNow,
            EndedAtUtc: null,
            TaskTitleSnapshot: taskTitle,
            SubjectNameSnapshot: subjectName,
            XpEarned: 0,
            IsInterrupted: false);

        _activeSession = newSession;
        return Task.FromResult(EntityResult.Ok(newSession.Id));
    }

    public Task<SessionCompletionOutcome> CompleteFocusSessionAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds = 0,
        int protectionGapSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? _activeSession;
        if (session is null || session.Id != sessionId)
        {
            return Task.FromResult(SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "Session not found."));
        }

        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Code, session.ProfileCode, StringComparison.OrdinalIgnoreCase))
                      ?? _profiles[0];

        var rate = profile.XpPerActiveMinute;
        var activeMinutes = activeStudySeconds / 60;
        var rawAward = activeMinutes * rate;
        var award = Math.Min(rawAward, _progress.SessionCapRemainingToday);
        var totalXpBefore = _progress.TotalXp;
        var levelBefore = _progress.Level;
        var totalXpAfter = totalXpBefore + award;
        var levelAfter = EconomyRules.LevelForTotalXp(totalXpAfter);
        var streakQualified = EconomyRules.IsStreakQualified(SessionState.Completed, activeStudySeconds);

        var completedSummary = session with
        {
            ElapsedSeconds = elapsedSeconds,
            ActiveStudySeconds = activeStudySeconds,
            State = SessionState.Completed,
            EndedAtUtc = DateTime.UtcNow,
            XpEarned = award
        };

        _sessions.RemoveAll(s => s.Id == sessionId);
        _sessions.Insert(0, completedSummary);
        if (_activeSession?.Id == sessionId)
        {
            _activeSession = null;
        }

        var newStreak = streakQualified ? _progress.CurrentStreakDays + 1 : _progress.CurrentStreakDays;
        var newSessionXpToday = _progress.SessionXpToday + award;

        _progress = _progress with
        {
            TotalXp = totalXpAfter,
            Level = levelAfter,
            CurrentStreakDays = newStreak,
            LongestStreakDays = Math.Max(newStreak, _progress.LongestStreakDays),
            QualifiedSessionCount = streakQualified ? _progress.QualifiedSessionCount + 1 : _progress.QualifiedSessionCount,
            SessionXpToday = newSessionXpToday,
            SessionCapRemainingToday = Math.Max(0, EconomyRules.DailySessionXpCap - newSessionXpToday)
        };

        var outcome = new SessionCompletionOutcome(
            Success: true,
            Error: null,
            Changed: true,
            XpAwarded: award,
            TotalXpAfter: totalXpAfter,
            LevelBefore: levelBefore,
            LevelAfter: levelAfter,
            IsStreakQualified: streakQualified,
            AchievementsUnlocked: Array.Empty<string>());

        return Task.FromResult(outcome);
    }

    public Task<SessionCompletionOutcome> EndFocusEarlyAsync(
        Guid sessionId,
        int elapsedSeconds,
        int activeStudySeconds,
        int idleSeconds,
        int protectionGapSeconds,
        FocusEndReason reason,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? _activeSession;
        if (session is null || session.Id != sessionId)
        {
            return Task.FromResult(SessionCompletionOutcome.Failed(UseCaseErrorCodes.SessionNotFound, "Session not found."));
        }

        var profile = _profiles.FirstOrDefault(p => string.Equals(p.Code, session.ProfileCode, StringComparison.OrdinalIgnoreCase))
                      ?? _profiles[0];

        var rate = profile.XpPerActiveMinute;
        var activeMinutes = activeStudySeconds / 60;
        var rawAward = (int)Math.Floor(activeMinutes * rate * 0.5);
        var award = Math.Min(rawAward, _progress.SessionCapRemainingToday);
        var totalXpBefore = _progress.TotalXp;
        var levelBefore = _progress.Level;
        var totalXpAfter = totalXpBefore + award;
        var levelAfter = EconomyRules.LevelForTotalXp(totalXpAfter);

        var endedSummary = session with
        {
            ElapsedSeconds = elapsedSeconds,
            ActiveStudySeconds = activeStudySeconds,
            State = SessionState.EndedEarly,
            EndedAtUtc = DateTime.UtcNow,
            XpEarned = award
        };

        _sessions.RemoveAll(s => s.Id == sessionId);
        _sessions.Insert(0, endedSummary);
        if (_activeSession?.Id == sessionId)
        {
            _activeSession = null;
        }

        var newSessionXpToday = _progress.SessionXpToday + award;
        _progress = _progress with
        {
            TotalXp = totalXpAfter,
            Level = levelAfter,
            SessionXpToday = newSessionXpToday,
            SessionCapRemainingToday = Math.Max(0, EconomyRules.DailySessionXpCap - newSessionXpToday)
        };

        var outcome = new SessionCompletionOutcome(
            Success: true,
            Error: null,
            Changed: true,
            XpAwarded: award,
            TotalXpAfter: totalXpAfter,
            LevelBefore: levelBefore,
            LevelAfter: levelAfter,
            IsStreakQualified: false,
            AchievementsUnlocked: Array.Empty<string>());

        return Task.FromResult(outcome);
    }

    public Task<IReadOnlyList<FocusSessionSummary>> ListSessionHistoryAsync(int maxCount = 50, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<FocusSessionSummary>>(_sessions.Take(maxCount).ToList().AsReadOnly());
    }

    public Task<IReadOnlyList<StudyTaskSummary>> ListTasksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudyTaskSummary>>(_tasks.AsReadOnly());

    public Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SubjectSummary>>(_subjects.AsReadOnly());
}
