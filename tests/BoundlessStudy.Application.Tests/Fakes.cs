using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// In-memory fakes for the application ports. Fakes mutate immediately so derived
/// reads inside the same use case see uncommitted state, mirroring the required
/// same-transaction behavior (§8.11). No database or filesystem is involved.
/// </summary>
public sealed class FakeClock : ILocalClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    public int UtcOffsetMinutes { get; set; }

    public DateOnly ToLocalDate(DateTime utcTime) => DateOnly.FromDateTime(utcTime.AddMinutes(UtcOffsetMinutes));

    public DateOnly Today => ToLocalDate(UtcNow);
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int BeginCount { get; private set; }
    public int CommitCount { get; private set; }
    public int RollbackCount { get; private set; }

    public void ResetCounts()
    {
        BeginCount = 0;
        CommitCount = 0;
        RollbackCount = 0;
    }

    public Task BeginAsync(CancellationToken cancellationToken = default)
    {
        BeginCount++;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        CommitCount++;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        RollbackCount++;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLocalProfileRepository : ILocalProfileRepository
{
    private readonly List<LocalProfile> _profiles = [];

    public Task<LocalProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_profiles.SingleOrDefault(p => p.Id == id));

    public Task<LocalProfile?> FindSingleAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_profiles.FirstOrDefault());

    public Task AddAsync(LocalProfile profile, CancellationToken cancellationToken = default)
    {
        _profiles.Add(profile);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySubjectRepository : ISubjectRepository
{
    private readonly List<Subject> _subjects = [];

    public Task<Subject?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_subjects.SingleOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<Subject>> ListAsync(Guid localProfileId, bool includeArchived, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Subject>>(_subjects
            .Where(s => s.LocalProfileId == localProfileId && (includeArchived || !s.IsArchived))
            .ToArray());

    public Task AddAsync(Subject subject, CancellationToken cancellationToken = default)
    {
        _subjects.Add(subject);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryStudyTaskRepository : IStudyTaskRepository
{
    private readonly List<StudyTask> _tasks = [];

    public IEnumerable<StudyTask> All => _tasks;

    public Task<StudyTask?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks.SingleOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<StudyTask>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudyTask>>(_tasks.Where(t => t.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(StudyTask task, CancellationToken cancellationToken = default)
    {
        _tasks.Add(task);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryFocusProfileRepository : IFocusProfileRepository
{
    private readonly List<FocusProfile> _profiles = [.. BuiltInProfiles.Seed()];

    public Task<FocusProfile?> FindAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_profiles.SingleOrDefault(p => p.Code == code));

    public Task<IReadOnlyList<FocusProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FocusProfile>>(_profiles.ToArray());

    public Task AddAsync(FocusProfile profile, CancellationToken cancellationToken = default)
    {
        _profiles.Add(profile);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryFocusSessionRepository : IFocusSessionRepository
{
    private readonly List<FocusSession> _sessions = [];

    public IEnumerable<FocusSession> All => _sessions;

    public Task<FocusSession?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.SingleOrDefault(s => s.Id == id));

    public Task<FocusSession?> FindActiveByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.FirstOrDefault(s =>
            s.LocalProfileId == localProfileId
            && s.State is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused or SessionState.RecoverableInterrupted));

    public Task<FocusSession?> FindActiveByTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.FirstOrDefault(s =>
            s.TaskId == taskId
            && s.State is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused or SessionState.RecoverableInterrupted));

    public Task<IReadOnlyList<FocusSession>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FocusSession>>(_sessions.Where(s => s.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(FocusSession session, CancellationToken cancellationToken = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBreakSessionRepository : IBreakSessionRepository
{
    private readonly List<BreakSession> _sessions = [];

    public Task<BreakSession?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.SingleOrDefault(s => s.Id == id));

    public Task<BreakSession?> FindActiveByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.FirstOrDefault(s => s.LocalProfileId == localProfileId && s.State == BreakState.Active));

    public Task AddAsync(BreakSession session, CancellationToken cancellationToken = default)
    {
        _sessions.Add(session);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySessionEventRepository : ISessionEventRepository
{
    private readonly List<SessionEvent> _events = [];

    public IReadOnlyList<SessionEvent> All => _events;

    public Task<int> MaxSequenceAsync(SessionType sessionType, Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_events
            .Where(e => e.SessionType == sessionType && e.SessionId == sessionId)
            .Select(e => e.Sequence)
            .DefaultIfEmpty(0)
            .Max());

    public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(SessionType sessionType, Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionEvent>>(_events
            .Where(e => e.SessionType == sessionType && e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToArray());

    public Task AddAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(sessionEvent);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySessionExceptionRepository : ISessionExceptionRepository
{
    private readonly List<SessionException> _exceptions = [];

    public IReadOnlyList<SessionException> All => _exceptions;

    public Task<int> CountBySessionAsync(Guid focusSessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_exceptions.Count(e => e.FocusSessionId == focusSessionId));

    public Task<IReadOnlyList<SessionException>> ListBySessionAsync(Guid focusSessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionException>>(_exceptions.Where(e => e.FocusSessionId == focusSessionId).ToArray());

    public Task AddAsync(SessionException exception, CancellationToken cancellationToken = default)
    {
        _exceptions.Add(exception);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBlockedAppRepository : IBlockedAppRepository
{
    private readonly List<BlockedApp> _apps = [];

    public Task<BlockedApp?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_apps.SingleOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<BlockedApp>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BlockedApp>>(_apps.Where(a => a.LocalProfileId == localProfileId).ToArray());

    public Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_apps.Count(a => a.LocalProfileId == localProfileId && a.IsEnabled));

    public Task AddAsync(BlockedApp app, CancellationToken cancellationToken = default)
    {
        _apps.Add(app);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(BlockedApp app, CancellationToken cancellationToken = default)
    {
        _apps.Remove(app);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBlockedSiteRepository : IBlockedSiteRepository
{
    private readonly List<BlockedSite> _sites = [];

    public Task<BlockedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sites.SingleOrDefault(s => s.Id == id));

    public Task<BlockedSite?> FindByDomainAsync(Guid localProfileId, string normalizedDomain, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sites.SingleOrDefault(s => s.LocalProfileId == localProfileId && s.NormalizedDomain == normalizedDomain));

    public Task<IReadOnlyList<BlockedSite>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BlockedSite>>(_sites.Where(s => s.LocalProfileId == localProfileId).ToArray());

    public Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sites.Count(s => s.LocalProfileId == localProfileId && s.IsEnabled));

    public Task AddAsync(BlockedSite site, CancellationToken cancellationToken = default)
    {
        _sites.Add(site);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(BlockedSite site, CancellationToken cancellationToken = default)
    {
        _sites.Remove(site);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAllowedSiteRepository : IAllowedSiteRepository
{
    private readonly List<AllowedSite> _sites = [];

    public Task<AllowedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sites.SingleOrDefault(s => s.Id == id));

    public Task<AllowedSite?> FindByDomainAsync(Guid localProfileId, string normalizedDomain, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sites.SingleOrDefault(s => s.LocalProfileId == localProfileId && s.NormalizedDomain == normalizedDomain));

    public Task<IReadOnlyList<AllowedSite>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AllowedSite>>(_sites.Where(s => s.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(AllowedSite site, CancellationToken cancellationToken = default)
    {
        _sites.Add(site);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(AllowedSite site, CancellationToken cancellationToken = default)
    {
        _sites.Remove(site);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRewardLedgerRepository : IRewardLedgerRepository
{
    private readonly List<RewardLedgerEntry> _entries = [];

    public IReadOnlyList<RewardLedgerEntry> All => _entries;

    public Task<RewardLedgerEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.SingleOrDefault(e => e.IdempotencyKey == idempotencyKey));

    public Task<int> SumSessionXpForLocalDateAsync(Guid localProfileId, DateOnly awardLocalDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .Where(e => e.LocalProfileId == localProfileId
                && e.AwardLocalDate == awardLocalDate
                && e.CurrencyCode == CurrencyCode.Xp
                && e.SourceType is LedgerSourceType.SessionCompletion or LedgerSourceType.SessionPartial)
            .Sum(e => e.Amount));

    public Task<long> SumXpAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries
            .Where(e => e.LocalProfileId == localProfileId && e.CurrencyCode == CurrencyCode.Xp)
            .Sum(e => (long)e.Amount));

    public Task<IReadOnlyList<RewardLedgerEntry>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RewardLedgerEntry>>(_entries.Where(e => e.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(RewardLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        if (_entries.Any(e => e.IdempotencyKey == entry.IdempotencyKey))
        {
            throw new DomainException("LEDGER_DUPLICATE_KEY", "A ledger entry with this idempotency key already exists.");
        }

        _entries.Add(entry);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAchievementRepository : IAchievementRepository
{
    private readonly List<AchievementDefinition> _definitions = [.. MvpAchievements.Seed()];

    public Task<AchievementDefinition?> FindAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_definitions.SingleOrDefault(d => d.Code == code));

    public Task<IReadOnlyList<AchievementDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AchievementDefinition>>(_definitions.ToArray());

    public Task AddAsync(AchievementDefinition definition, CancellationToken cancellationToken = default)
    {
        _definitions.Add(definition);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryUserAchievementRepository : IUserAchievementRepository
{
    private readonly List<UserAchievement> _achievements = [];

    public IReadOnlyList<UserAchievement> All => _achievements;

    public Task<UserAchievement?> FindAsync(Guid localProfileId, string achievementCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(_achievements.SingleOrDefault(a => a.LocalProfileId == localProfileId && a.AchievementCode == achievementCode));

    public Task<IReadOnlyList<UserAchievement>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserAchievement>>(_achievements.Where(a => a.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(UserAchievement achievement, CancellationToken cancellationToken = default)
    {
        _achievements.Add(achievement);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAppSettingRepository : IAppSettingRepository
{
    private readonly List<AppSetting> _settings = [];

    public Task<AppSetting?> FindAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings.SingleOrDefault(s => s.Key == key));

    public Task AddAsync(AppSetting setting, CancellationToken cancellationToken = default)
    {
        _settings.Add(setting);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBackupRecordRepository : IBackupRecordRepository
{
    private readonly List<BackupRecord> _records = [];

    public Task<IReadOnlyList<BackupRecord>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BackupRecord>>(_records.Where(r => r.LocalProfileId == localProfileId).ToArray());

    public Task AddAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }
}

/// <summary>Derives qualified counts and streak dates from primary data (never caches).</summary>
public sealed class FakeRewardStateProvider : IRewardStateProvider
{
    private readonly InMemoryFocusSessionRepository _sessions;
    private readonly InMemoryStudyTaskRepository _tasks;
    private readonly FakeClock _clock;

    public FakeRewardStateProvider(
        InMemoryFocusSessionRepository sessions,
        InMemoryStudyTaskRepository tasks,
        FakeClock clock)
    {
        _sessions = sessions;
        _tasks = tasks;
        _clock = clock;
    }

    public Task<int> GetQualifiedSessionCountAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.All.Count(s =>
            s.LocalProfileId == localProfileId
            && s.State == SessionState.Completed
            && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds));

    public Task<int> GetStrictQualifiedSessionCountAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.All.Count(s =>
            s.LocalProfileId == localProfileId
            && s.ProfileCode == BuiltInProfiles.StrictCode
            && s.State == SessionState.Completed
            && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds));

    // ACH-002 counts recorded active-study time from every terminal outcome, but not
    // from a session that is still Active/Blocked/SystemPaused/RecoverableInterrupted.
    public Task<int> GetTotalActiveStudyMinutesAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.All
            .Where(s => s.LocalProfileId == localProfileId
                && (s.State is SessionState.Completed or SessionState.EndedEarly or SessionState.Abandoned))
            .Sum(s => s.ActiveStudySeconds) / 60);

    public Task<int> GetCompletedTaskCountAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks.All.Count(t => t.LocalProfileId == localProfileId && t.CompletedAtUtc is not null));

    public Task<IReadOnlyList<DateOnly>> ListQualifiedLocalDatesAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DateOnly>>(_sessions.All
            .Where(s => s.LocalProfileId == localProfileId
                && s.State == SessionState.Completed
                && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds
                && s.EndedAtUtc is not null)
            .Select(s => _clock.ToLocalDate(s.EndedAtUtc!.Value))
            .Distinct()
            .ToArray());
}
