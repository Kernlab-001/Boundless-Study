using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// The study database. Only the per-user agent constructs this context
/// (system-architecture.md §4, §6; ADR-0005). The model is created only after the
/// schema-version handshake has accepted the applied migrations.
/// </summary>
public sealed class BoundlessDbContext : DbContext
{
    public BoundlessDbContext(DbContextOptions<BoundlessDbContext> options)
        : base(options)
    {
    }

    public DbSet<LocalProfile> LocalProfiles => Set<LocalProfile>();

    public DbSet<Subject> Subjects => Set<Subject>();

    public DbSet<StudyTask> StudyTasks => Set<StudyTask>();

    public DbSet<FocusProfile> FocusProfiles => Set<FocusProfile>();

    public DbSet<FocusSession> FocusSessions => Set<FocusSession>();

    public DbSet<BreakSession> BreakSessions => Set<BreakSession>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    public DbSet<SessionException> SessionExceptions => Set<SessionException>();

    public DbSet<BlockedApp> BlockedApps => Set<BlockedApp>();

    public DbSet<BlockedSite> BlockedSites => Set<BlockedSite>();

    public DbSet<AllowedSite> AllowedSites => Set<AllowedSite>();

    public DbSet<RewardLedgerEntry> RewardLedgerEntries => Set<RewardLedgerEntry>();

    public DbSet<AchievementDefinition> AchievementDefinitions => Set<AchievementDefinition>();

    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BoundlessDbContext).Assembly);
    }
}
