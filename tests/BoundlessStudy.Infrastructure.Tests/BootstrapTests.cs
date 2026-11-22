using BoundlessStudy.Domain;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class BootstrapTests
{
    [Fact]
    public async Task Fresh_database_applies_the_initial_migration_seeds_and_enables_wal()
    {
        await using var db = await TestDatabase.CreateAsync();

        var bootstrap = db.Store.Bootstrap;
        Assert.NotNull(bootstrap);
        Assert.True(bootstrap.Migration.DatabaseCreated);
        Assert.Contains(bootstrap.Migration.PendingBeforeMigration, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Contains(bootstrap.Migration.PendingBeforeMigration, m => m.EndsWith("_AddAppSettingUpdatedAtIndex", StringComparison.Ordinal));
        Assert.Contains(bootstrap.Migration.PendingBeforeMigration, m => m.EndsWith("_AddFocusSessionInterruptedAtUtc", StringComparison.Ordinal));
        Assert.Equal(3, bootstrap.Migration.AppliedMigrations.Count);
        Assert.EndsWith("_InitialCreate", bootstrap.Migration.AppliedMigrations[0], StringComparison.Ordinal);
        Assert.EndsWith("_AddAppSettingUpdatedAtIndex", bootstrap.Migration.AppliedMigrations[1], StringComparison.Ordinal);
        Assert.EndsWith("_AddFocusSessionInterruptedAtUtc", bootstrap.Migration.AppliedMigrations[2], StringComparison.Ordinal);
        Assert.Null(bootstrap.Migration.PreMigrationBackup);
        Assert.True(bootstrap.Seed.LocalProfileCreated);
        Assert.Equal(3, bootstrap.Seed.FocusProfilesCreated);
        Assert.Equal(5, bootstrap.Seed.AchievementDefinitionsCreated);
    }

    [Fact]
    public async Task Bootstrap_seeds_exactly_one_profile_three_focus_profiles_and_five_achievements()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();

        Assert.Equal(1, await repositories.Context.LocalProfiles.CountAsync());
        Assert.Equal(3, await repositories.Context.FocusProfiles.CountAsync());
        Assert.Equal(5, await repositories.Context.AchievementDefinitions.CountAsync());

        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);
        Assert.Equal("My study profile", profile.DisplayName);
        Assert.Equal(InstallationMode.Limited, profile.InstallationMode);
        Assert.False(profile.IsOnboarded);
        Assert.Equal(0, profile.CachedTotalXp);
        Assert.Equal(1, profile.CachedLevel);

        var profiles = await repositories.FocusProfiles.ListAsync();
        Assert.Equal(new[] { "Balanced", "Flexible", "Strict" }, profiles.Select(p => p.Code));
        Assert.Equal(3, profiles.Single(p => p.Code == "Balanced").XpPerActiveMinute);
        Assert.Equal(2, profiles.Single(p => p.Code == "Flexible").XpPerActiveMinute);
        Assert.Equal(4, profiles.Single(p => p.Code == "Strict").XpPerActiveMinute);

        var achievements = await repositories.Achievements.ListAsync();
        Assert.Equal(new[] { "ACH-001", "ACH-002", "ACH-003", "ACH-004", "ACH-005" },
            achievements.Select(a => a.Code));
        Assert.Equal(new[] { 50, 75, 100, 100, 75 }, achievements.Select(a => a.XpAward));
        Assert.Equal(new[] { 1, 60, 3, 5, 10 }, achievements.Select(a => a.Threshold));
        Assert.Equal(
            new[]
            {
                AchievementCriteriaType.SessionCount,
                AchievementCriteriaType.ActiveMinutes,
                AchievementCriteriaType.StreakDays,
                AchievementCriteriaType.StrictSessionCount,
                AchievementCriteriaType.TaskCount,
            },
            achievements.Select(a => a.CriteriaType));
    }

    [Fact]
    public async Task Reopening_an_initialized_database_is_idempotent_and_does_not_duplicate_seed_rows()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var reopened = new SqliteDataStore(db.Options, db.Clock);
        try
        {
            var second = await reopened.InitializeAsync();

            Assert.False(second.Migration.DatabaseCreated);
            Assert.Empty(second.Migration.PendingBeforeMigration);
            Assert.Equal(profileId, second.Seed.LocalProfileId);
            Assert.False(second.Seed.LocalProfileCreated);
            Assert.Equal(0, second.Seed.FocusProfilesCreated);
            Assert.Equal(0, second.Seed.AchievementDefinitionsCreated);
            Assert.Null(second.Migration.PreMigrationBackup);
            Assert.Null(second.BackupRecordWarning);
        }
        finally
        {
            await reopened.DisposeAsync();
        }

        await using var repositories = db.Store.CreateRepositories();
        Assert.Equal(1, await repositories.Context.LocalProfiles.CountAsync());
        Assert.Equal(3, await repositories.Context.FocusProfiles.CountAsync());
        Assert.Equal(5, await repositories.Context.AchievementDefinitions.CountAsync());
    }

    [Fact]
    public async Task Database_reports_integrity_ok_and_wal_journal_mode()
    {
        await using var db = await TestDatabase.CreateAsync();

        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
        Assert.Equal("wal", (await db.TextAsync("PRAGMA journal_mode;"))?.ToLowerInvariant());
        Assert.Equal(1, await db.ScalarAsync("PRAGMA foreign_keys;"));
    }
}
