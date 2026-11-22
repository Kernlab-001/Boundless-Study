using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class RepositoryTests
{
    [Fact]
    public async Task All_sixteen_repository_ports_round_trip_their_entity()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);
        Assert.Equal(profile.Id, (await repositories.LocalProfiles.FindAsync(profile.Id))!.Id);

        // Subject.
        var subject = Subject.Create(profile.Id, "Mathematics", "Algebra", db.Clock.UtcNow);
        await repositories.Subjects.AddAsync(subject);
        Assert.Equal(subject.Id, (await repositories.Subjects.FindAsync(subject.Id))!.Id);
        Assert.Single(await repositories.Subjects.ListAsync(profile.Id, includeArchived: false));
        subject.Archive(db.Clock.UtcNow);
        await repositories.Context.SaveChangesAsync();
        Assert.Empty(await repositories.Subjects.ListAsync(profile.Id, includeArchived: false));
        Assert.Single(await repositories.Subjects.ListAsync(profile.Id, includeArchived: true));

        // FocusProfile (seeded).
        Assert.Equal(3, (await repositories.FocusProfiles.ListAsync()).Count);
        Assert.Equal(3, (await repositories.FocusProfiles.FindAsync(BuiltInProfiles.BalancedCode))!.XpPerActiveMinute);

        // StudyTask.
        var task = StudyTask.Create(
            profile.Id,
            "Solve equations",
            db.Clock.UtcNow,
            subjectId: subject.Id,
            dueDateLocal: new DateOnly(2026, 11, 20),
            estimatedMinutes: 30,
            priority: TaskPriority.High);
        await repositories.StudyTasks.AddAsync(task);
        Assert.Equal(task.Id, (await repositories.StudyTasks.FindAsync(task.Id))!.Id);
        Assert.Single(await repositories.StudyTasks.ListAsync(profile.Id));

        // RewardLedgerEntry.
        var ledgerEntry = RewardLedgerEntry.Create(
            profile.Id,
            CurrencyCode.Xp,
            45,
            LedgerSourceType.SessionCompletion,
            "session-1",
            db.Clock.UtcNow,
            new DateOnly(2026, 11, 14),
            0,
            EconomyRules.Version);
        await repositories.RewardLedger.AddAsync(ledgerEntry);
        Assert.Equal(ledgerEntry.Id,
            (await repositories.RewardLedger.FindByIdempotencyKeyAsync(ledgerEntry.IdempotencyKey))!.Id);
        Assert.Equal(45, await repositories.RewardLedger.SumSessionXpForLocalDateAsync(
            profile.Id, new DateOnly(2026, 11, 14)));
        Assert.Equal(45, await repositories.RewardLedger.SumXpAsync(profile.Id));
        Assert.Single(await repositories.RewardLedger.ListByProfileAsync(profile.Id));

        // FocusSession.
        var session = FocusSession.Start(
            profile.Id,
            BuiltInProfiles.BalancedCode,
            600,
            db.Clock.UtcNow,
            EconomyRules.Version,
            taskId: task.Id,
            subjectId: subject.Id,
            taskTitleSnapshot: task.Title,
            subjectNameSnapshot: subject.Name);
        session.ApplyCheckpoint(db.Clock.UtcNow.AddMinutes(10), 600, 580, 20, 0, 0, 0, 0);
        await repositories.FocusSessions.AddAsync(session);
        Assert.Equal(session.Id, (await repositories.FocusSessions.FindAsync(session.Id))!.Id);
        Assert.Equal(session.Id, (await repositories.FocusSessions.FindActiveByProfileAsync(profile.Id))!.Id);
        Assert.Equal(session.Id, (await repositories.FocusSessions.FindActiveByTaskAsync(task.Id))!.Id);
        Assert.Single(await repositories.FocusSessions.ListByProfileAsync(profile.Id));

        // BreakSession.
        var breakSession = BreakSession.Start(
            profile.Id, 300, db.Clock.UtcNow, precedingFocusSessionId: session.Id);
        await repositories.BreakSessions.AddAsync(breakSession);
        Assert.Equal(breakSession.Id, (await repositories.BreakSessions.FindAsync(breakSession.Id))!.Id);
        Assert.Equal(breakSession.Id, (await repositories.BreakSessions.FindActiveByProfileAsync(profile.Id))!.Id);

        // SessionEvent.
        var sessionEvent = SessionEvent.Create(
            SessionType.Focus,
            session.Id,
            1,
            SessionEventType.Start,
            db.Clock.UtcNow,
            new DateOnly(2026, 11, 14),
            0,
            string.Empty,
            db.Clock.UtcNow);
        await repositories.SessionEvents.AddAsync(sessionEvent);
        Assert.Equal(1, await repositories.SessionEvents.MaxSequenceAsync(SessionType.Focus, session.Id));
        Assert.Single(await repositories.SessionEvents.ListBySessionAsync(SessionType.Focus, session.Id));

        // SessionException.
        var exception = SessionException.Create(
            session.Id,
            ExceptionTargetType.Application,
            "excel.exe",
            db.Clock.UtcNow,
            db.Clock.UtcNow.AddMinutes(5),
            ExceptionReasonCategory.StudyResource,
            db.Clock.UtcNow);
        await repositories.SessionExceptions.AddAsync(exception);
        Assert.Equal(1, await repositories.SessionExceptions.CountBySessionAsync(session.Id));
        Assert.Single(await repositories.SessionExceptions.ListBySessionAsync(session.Id));

        // BlockedApp.
        var blockedApp = BlockedApp.Create(
            profile.Id, "Notepad", @"C:\Tools\Notepad.exe", null, db.Clock.UtcNow);
        await repositories.BlockedApps.AddAsync(blockedApp);
        Assert.Single(await repositories.BlockedApps.ListAsync(profile.Id));
        Assert.Equal(1, await repositories.BlockedApps.CountEnabledAsync(profile.Id));

        // BlockedSite.
        var blockedSite = BlockedSite.Create(profile.Id, "example.com", "Example", db.Clock.UtcNow);
        await repositories.BlockedSites.AddAsync(blockedSite);
        Assert.Equal(blockedSite.Id,
            (await repositories.BlockedSites.FindByDomainAsync(profile.Id, "example.com"))!.Id);
        Assert.Equal(1, await repositories.BlockedSites.CountEnabledAsync(profile.Id));

        // AllowedSite.
        var allowedSite = AllowedSite.Create(profile.Id, "khanacademy.org", "Khan", db.Clock.UtcNow);
        await repositories.AllowedSites.AddAsync(allowedSite);
        Assert.Equal(allowedSite.Id,
            (await repositories.AllowedSites.FindByDomainAsync(profile.Id, "khanacademy.org"))!.Id);

        // AchievementDefinition (seeded).
        Assert.Equal("First Focus", (await repositories.Achievements.FindAsync("ACH-001"))!.Name);
        Assert.Equal(5, (await repositories.Achievements.ListAsync()).Count);

        // UserAchievement.
        var unlock = UserAchievement.Create(profile.Id, "ACH-001", db.Clock.UtcNow, ledgerEntry.Id);
        await repositories.UserAchievements.AddAsync(unlock);
        Assert.Equal(unlock.Id,
            (await repositories.UserAchievements.FindAsync(profile.Id, "ACH-001"))!.Id);
        Assert.Single(await repositories.UserAchievements.ListByProfileAsync(profile.Id));

        // AppSetting.
        var setting = AppSetting.Create("theme", "{\"mode\":\"light\"}", db.Clock.UtcNow);
        await repositories.AppSettings.AddAsync(setting);
        Assert.Equal(setting.ValueJson, (await repositories.AppSettings.FindAsync("theme"))!.ValueJson);

        // BackupRecord.
        var backup = BackupRecord.Create(
            profile.Id,
            "boundless-backup-manual-test.db",
            BackupReason.Manual,
            "InitialCreate",
            "0.1.0",
            new string('a', 64),
            4096,
            db.Clock.UtcNow);
        backup.MarkVerified(db.Clock.UtcNow);
        await repositories.BackupRecords.AddAsync(backup);
        var records = await repositories.BackupRecords.ListAsync(profile.Id);
        Assert.Single(records);
        Assert.Equal(backup.Id, records[0].Id);
        Assert.Equal(64, records[0].Sha256.Length);
    }

    [Fact]
    public async Task Unique_constraints_reject_duplicate_ledger_keys_domains_and_achievement_unlocks()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);

        var first = RewardLedgerEntry.Create(
            profile.Id,
            CurrencyCode.Xp,
            10,
            LedgerSourceType.Adjustment,
            "source-1",
            db.Clock.UtcNow,
            new DateOnly(2026, 11, 14),
            0,
            EconomyRules.Version);
        await repositories.RewardLedger.AddAsync(first);

        var duplicateKey = RewardLedgerEntry.Create(
            profile.Id,
            CurrencyCode.Xp,
            10,
            LedgerSourceType.Adjustment,
            "source-1",
            db.Clock.UtcNow,
            new DateOnly(2026, 11, 14),
            0,
            EconomyRules.Version);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => repositories.RewardLedger.AddAsync(duplicateKey));
        repositories.Context.ChangeTracker.Clear();

        await repositories.BlockedSites.AddAsync(
            BlockedSite.Create(profile.Id, "example.com", null, db.Clock.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => repositories.BlockedSites.AddAsync(
            BlockedSite.Create(profile.Id, "www.example.com", null, db.Clock.UtcNow)));
        repositories.Context.ChangeTracker.Clear();

        var unlock = UserAchievement.Create(profile.Id, "ACH-001", db.Clock.UtcNow);
        await repositories.UserAchievements.AddAsync(unlock);
        await Assert.ThrowsAsync<DbUpdateException>(() => repositories.UserAchievements.AddAsync(
            UserAchievement.Create(profile.Id, "ACH-001", db.Clock.UtcNow)));
    }
}
