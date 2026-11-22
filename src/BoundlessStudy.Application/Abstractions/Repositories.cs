using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Abstractions;

// Repository ports. Infrastructure implements these in a later milestone
// (SQLite + EF Core, Milestone 3). The agent is the only component that
// constructs concrete implementations; every operation below runs inside the
// unit-of-work transaction when one is active.

/// <summary>LocalProfile persistence (conceptual-data-model.md §4.1).</summary>
public interface ILocalProfileRepository
{
    Task<LocalProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LocalProfile?> FindSingleAsync(CancellationToken cancellationToken = default);

    Task AddAsync(LocalProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Subject persistence (§4.2). Archived subjects remain readable.</summary>
public interface ISubjectRepository
{
    Task<Subject?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Subject>> ListAsync(Guid localProfileId, bool includeArchived, CancellationToken cancellationToken = default);

    Task AddAsync(Subject subject, CancellationToken cancellationToken = default);
}

/// <summary>StudyTask persistence (§4.3). Soft-deleted tasks are retained.</summary>
public interface IStudyTaskRepository
{
    Task<StudyTask?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudyTask>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(StudyTask task, CancellationToken cancellationToken = default);
}

/// <summary>FocusProfile reference data (§4.4). Seeded from BuiltInProfiles.</summary>
public interface IFocusProfileRepository
{
    Task<FocusProfile?> FindAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FocusProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(FocusProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>FocusSession persistence (§4.5) with the one-active-session lookups.</summary>
public interface IFocusSessionRepository
{
    Task<FocusSession?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The one active (Active/Blocked/SystemPaused/RecoverableInterrupted) session for the profile, if any.</summary>
    Task<FocusSession?> FindActiveByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    /// <summary>Any non-terminal session for the task, for the one-task-link-at-a-time rule (§9.2).</summary>
    Task<FocusSession?> FindActiveByTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FocusSession>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(FocusSession session, CancellationToken cancellationToken = default);
}

/// <summary>BreakSession persistence (§4.6).</summary>
public interface IBreakSessionRepository
{
    Task<BreakSession?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BreakSession?> FindActiveByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(BreakSession session, CancellationToken cancellationToken = default);
}

/// <summary>Append-only SessionEvent persistence (§4.7) with per-session sequence lookup.</summary>
public interface ISessionEventRepository
{
    Task<int> MaxSequenceAsync(SessionType sessionType, Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(SessionType sessionType, Guid sessionId, CancellationToken cancellationToken = default);

    Task AddAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default);
}

/// <summary>SessionException persistence (§4.8).</summary>
public interface ISessionExceptionRepository
{
    Task<int> CountBySessionAsync(Guid focusSessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionException>> ListBySessionAsync(Guid focusSessionId, CancellationToken cancellationToken = default);

    Task AddAsync(SessionException exception, CancellationToken cancellationToken = default);
}

/// <summary>BlockedApp persistence (§4.9).</summary>
public interface IBlockedAppRepository
{
    Task<BlockedApp?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockedApp>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(BlockedApp app, CancellationToken cancellationToken = default);

    Task RemoveAsync(BlockedApp app, CancellationToken cancellationToken = default);
}

/// <summary>BlockedSite persistence (§4.10). Domain unique per profile and list type.</summary>
public interface IBlockedSiteRepository
{
    Task<BlockedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BlockedSite?> FindByDomainAsync(Guid localProfileId, string normalizedDomain, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockedSite>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(BlockedSite site, CancellationToken cancellationToken = default);

    Task RemoveAsync(BlockedSite site, CancellationToken cancellationToken = default);
}

/// <summary>AllowedSite persistence (§4.11). Precedence over blocked sites is a UI contract.</summary>
public interface IAllowedSiteRepository
{
    Task<AllowedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AllowedSite?> FindByDomainAsync(Guid localProfileId, string normalizedDomain, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AllowedSite>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(AllowedSite site, CancellationToken cancellationToken = default);

    Task RemoveAsync(AllowedSite site, CancellationToken cancellationToken = default);
}

/// <summary>Append-only RewardLedgerEntry persistence (§4.12) with idempotency and cap lookups.</summary>
public interface IRewardLedgerRepository
{
    /// <summary>Idempotent-award lookup (§8.10): the retry path returns the existing entry.</summary>
    Task<RewardLedgerEntry?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Sum of session XP (SessionCompletion + SessionPartial) already awarded for one local day (§8.4).</summary>
    Task<int> SumSessionXpForLocalDateAsync(Guid localProfileId, DateOnly awardLocalDate, CancellationToken cancellationToken = default);

    Task<long> SumXpAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RewardLedgerEntry>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(RewardLedgerEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>AchievementDefinition persistence (§4.13). Immutable criteria; seeded once.</summary>
public interface IAchievementRepository
{
    Task<AchievementDefinition?> FindAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AchievementDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(AchievementDefinition definition, CancellationToken cancellationToken = default);
}

/// <summary>UserAchievement persistence (§4.14). Unique (profile, code).</summary>
public interface IUserAchievementRepository
{
    Task<UserAchievement?> FindAsync(Guid localProfileId, string achievementCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserAchievement>> ListByProfileAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(UserAchievement achievement, CancellationToken cancellationToken = default);
}

/// <summary>AppSetting persistence (§4.15).</summary>
public interface IAppSettingRepository
{
    Task<AppSetting?> FindAsync(string key, CancellationToken cancellationToken = default);

    Task AddAsync(AppSetting setting, CancellationToken cancellationToken = default);
}

/// <summary>BackupRecord persistence (§4.16).</summary>
public interface IBackupRecordRepository
{
    Task<IReadOnlyList<BackupRecord>> ListAsync(Guid localProfileId, CancellationToken cancellationToken = default);

    Task AddAsync(BackupRecord record, CancellationToken cancellationToken = default);
}
