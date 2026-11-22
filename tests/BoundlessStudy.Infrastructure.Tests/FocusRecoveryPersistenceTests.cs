using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// Real-SQLite recovery coverage for A6: the interruption timestamp survives a
/// process restart, the pending query is safe, the 24-hour boundary is enforced
/// across restart, and repeated resume/discard/start calls never duplicate rewards.
/// </summary>
public sealed class FocusRecoveryPersistenceTests
{
    [Fact]
    public async Task Pending_interruption_survives_restart_and_expires_exactly_at_twenty_four_hours()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid sessionId;
        DateTime interruptedAt;

        await using (var repositories = db.Store.CreateRepositories())
        {
            sessionId = (await Start(repositories, db).HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500)).Id!.Value;
            Assert.True((await new ApplyFocusCheckpointHandler(
                repositories.FocusSessions, repositories.UnitOfWork, db.Clock)
                .HandleAsync(sessionId, 600, 600, 0, 0, 0, 0, 0)).Success);

            db.Clock.UtcNow = db.Clock.UtcNow.AddMinutes(10);
            interruptedAt = db.Clock.UtcNow;
            Assert.True((await new MarkFocusInterruptedHandler(
                repositories.FocusSessions, repositories.UnitOfWork, db.Clock,
                new SessionEventRecorder(repositories.SessionEvents, db.Clock))
                .HandleAsync(sessionId, FocusEndReason.AgentCrash)).Success);
        }

        // Restart: a brand-new store and repository scope over the same file.
        await using var restarted = new SqliteDataStore(db.Options, db.Clock);
        await restarted.InitializeAsync();

        await using (var repositories = restarted.CreateRepositories())
        {
            var pending = await new GetPendingFocusSessionHandler(
                repositories.LocalProfiles, repositories.FocusSessions, db.Clock)
                .HandleAsync(profileId);

            Assert.NotNull(pending);
            Assert.Equal(sessionId, pending.SessionId);
            Assert.Equal(interruptedAt, pending.InterruptedAtUtc);
            Assert.Equal(interruptedAt, pending.EffectiveInterruptionAnchorUtc);
            Assert.False(pending.IsExpired);
            Assert.Equal(TimeSpan.Zero, pending.Age);
        }

        // One second before the boundary a new start is still blocked.
        db.Clock.UtcNow = interruptedAt.AddHours(24).AddSeconds(-1);
        await using (var repositories = restarted.CreateRepositories())
        {
            var blocked = await Start(repositories, db).HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500);
            Assert.False(blocked.Success);
            Assert.Equal(UseCaseErrorCodes.RecoverableSessionPending, blocked.Error!.Code);
        }

        // Exactly at the boundary the old session is abandoned and the new one starts.
        db.Clock.UtcNow = interruptedAt.AddHours(24);
        Guid newSessionId;
        await using (var repositories = restarted.CreateRepositories())
        {
            var started = await Start(repositories, db).HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500);
            Assert.True(started.Success, started.Error?.Message);
            newSessionId = started.Id!.Value;

            var oldSession = await repositories.FocusSessions.FindAsync(sessionId);
            Assert.NotNull(oldSession);
            Assert.Equal(SessionState.Abandoned, oldSession.State);
            Assert.Equal(FocusEndReason.Unknown, oldSession.EndReason);
            Assert.Equal(interruptedAt, oldSession.InterruptedAtUtc);
            Assert.Equal(db.Clock.UtcNow, oldSession.EndedAtUtc);

            var partial = (await repositories.RewardLedger.ListByProfileAsync(profileId))
                .Where(e => e.SourceType == LedgerSourceType.SessionPartial).ToList();
            Assert.Single(partial);
            Assert.Equal(15, partial[0].Amount);
        }

        // A second restart preserves the terminal and active rows and creates no new reward.
        await using var afterExpiry = new SqliteDataStore(db.Options, db.Clock);
        await afterExpiry.InitializeAsync();
        await using (var repositories = afterExpiry.CreateRepositories())
        {
            var pending = await new GetPendingFocusSessionHandler(
                repositories.LocalProfiles, repositories.FocusSessions, db.Clock)
                .HandleAsync(profileId);
            Assert.Null(pending);

            var oldSession = await repositories.FocusSessions.FindAsync(sessionId);
            Assert.Equal(SessionState.Abandoned, oldSession!.State);
            var newSession = await repositories.FocusSessions.FindAsync(newSessionId);
            Assert.Equal(SessionState.Active, newSession!.State);

            var events = await repositories.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId);
            Assert.Equal(
                new[]
                {
                    SessionEventType.Start,
                    SessionEventType.Interrupted,
                    SessionEventType.Abandoned,
                    SessionEventType.RewardCreated,
                },
                events.OrderBy(e => e.Sequence).Select(e => e.EventType));

            var ledgerCount = (await repositories.RewardLedger.ListByProfileAsync(profileId)).Count;

            var discardAgain = await Abandon(repositories, db).HandleAsync(sessionId, FocusEndReason.Unknown);
            Assert.True(discardAgain.Success);
            Assert.False(discardAgain.Changed);
            Assert.Equal(ledgerCount, (await repositories.RewardLedger.ListByProfileAsync(profileId)).Count);
        }
    }

    private static StartFocusSessionHandler Start(SqliteRepositorySet repositories, TestDatabase db) =>
        new(repositories.LocalProfiles,
            repositories.FocusProfiles,
            repositories.FocusSessions,
            repositories.BreakSessions,
            repositories.StudyTasks,
            repositories.Subjects,
            repositories.UnitOfWork,
            db.Clock,
            new SessionEventRecorder(repositories.SessionEvents, db.Clock),
            Abandon(repositories, db));

    private static AbandonFocusSessionHandler Abandon(SqliteRepositorySet repositories, TestDatabase db) =>
        new(repositories.FocusSessions,
            db.Clock,
            new RecoverFocusSessionHandler(
                repositories.FocusSessions,
                repositories.LocalProfiles,
                repositories.UnitOfWork,
                new RewardService(
                    repositories.RewardLedger,
                    repositories.Achievements,
                    repositories.UserAchievements,
                    repositories.RewardState,
                    db.Clock),
                db.Clock,
                new SessionEventRecorder(repositories.SessionEvents, db.Clock)));
}
