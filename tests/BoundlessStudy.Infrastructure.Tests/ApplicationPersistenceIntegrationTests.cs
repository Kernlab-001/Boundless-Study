using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class ApplicationPersistenceIntegrationTests
{
    [Fact]
    public async Task Terminal_completion_commits_session_ledger_events_achievement_and_cached_totals_together()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var startedAt = db.Clock.UtcNow;

        Guid sessionId = Guid.Empty;
        var sessionAward = 0;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindAsync(profileId);
            Assert.NotNull(profile);

            var session = FocusSession.Start(
                profileId,
                BuiltInProfiles.BalancedCode,
                600,
                startedAt,
                EconomyRules.Version);
            sessionId = session.Id;
            await repositories.FocusSessions.AddAsync(session);

            var rewards = new RewardService(
                repositories.RewardLedger,
                repositories.Achievements,
                repositories.UserAchievements,
                repositories.RewardState,
                db.Clock);
            var events = new SessionEventRecorder(repositories.SessionEvents, db.Clock);
            var recovery = new RecoverFocusSessionHandler(
                repositories.FocusSessions,
                repositories.LocalProfiles,
                repositories.UnitOfWork,
                rewards,
                db.Clock,
                events);
            var complete = new CompleteFocusSessionHandler(repositories.FocusSessions, db.Clock, recovery);

            db.Clock.UtcNow = startedAt.AddMinutes(10);
            var outcome = await complete.HandleAsync(sessionId, 600, 600, 0, 0);

            Assert.True(outcome.Success);
            Assert.True(outcome.Changed);
            Assert.Equal(30, outcome.XpAwarded);
            Assert.Equal(80, outcome.TotalXpAfter);
            Assert.True(outcome.IsStreakQualified);
            Assert.Contains("ACH-001", outcome.AchievementsUnlocked);
            sessionAward = outcome.XpAwarded;
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            var session = await verification.FocusSessions.FindAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal(SessionState.Completed, session.State);
            Assert.Equal(600, session.ActiveStudySeconds);

            var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId);
            Assert.Equal(
                new[]
                {
                    SessionEventType.Completed,
                    SessionEventType.RewardCreated,
                    SessionEventType.AchievementUnlocked,
                },
                events.Select(e => e.EventType));

            var entries = await verification.RewardLedger.ListByProfileAsync(profileId);
            Assert.Equal(2, entries.Count);
            Assert.Equal(sessionAward, entries.Single(e =>
                e.SourceType == LedgerSourceType.SessionCompletion).Amount);
            Assert.Equal(50, entries.Single(e =>
                e.SourceType == LedgerSourceType.AchievementUnlock).Amount);

            var unlocks = await verification.UserAchievements.ListByProfileAsync(profileId);
            var unlock = Assert.Single(unlocks);
            Assert.Equal("ACH-001", unlock.AchievementCode);

            var profile = await verification.LocalProfiles.FindAsync(profileId);
            Assert.NotNull(profile);
            Assert.Equal(80, profile.CachedTotalXp);
            Assert.Equal(1, profile.CachedLevel);
        }
    }

    [Fact]
    public async Task System_pause_and_event_are_rolled_back_together_when_event_insert_fails()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var session = FocusSession.Start(
            profileId,
            BuiltInProfiles.BalancedCode,
            600,
            db.Clock.UtcNow,
            EconomyRules.Version);
        var sessionId = session.Id;

        await using (var repositories = db.Store.CreateRepositories())
        {
            await repositories.FocusSessions.AddAsync(session);
        }

        // Force a real SQLite failure after the domain transition has occurred but
        // before the use case can commit its state and corresponding event.
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailSystemPauseEvent BEFORE INSERT ON SessionEvent " +
                                  "WHEN NEW.EventType = 'SystemPaused' BEGIN SELECT RAISE(ABORT, 'test event failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using (var repositories = db.Store.CreateRepositories())
        {
            var events = new SessionEventRecorder(repositories.SessionEvents, db.Clock);
            var useCases = new SystemStateUseCases(
                repositories.FocusSessions,
                repositories.UnitOfWork,
                db.Clock,
                events);

            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
                () => useCases.HandleWorkstationLockAsync(sessionId, isLocked: true));
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            var persistedSession = await verification.FocusSessions.FindAsync(sessionId);
            Assert.NotNull(persistedSession);
            Assert.Equal(SessionState.Active, persistedSession.State);
            Assert.Empty(await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId));
        }
    }
}
