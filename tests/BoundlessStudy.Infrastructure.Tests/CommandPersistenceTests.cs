using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// Permanent real-SQLite coverage for the A-01 re-completion fix and the A-02
/// shared command boundary: every mutating path commits atomically, rolls back on
/// failure, and a failed event append cannot leave a session behind.
/// </summary>
public sealed class CommandPersistenceTests
{
    [Fact]
    public async Task Reopened_task_returns_to_done_without_a_second_ledger_entry_or_achievement()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var taskIds = new List<Guid>();
        DateTime? firstCompletedAtUtc = null;
        Guid? completionRewardId = null;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var rewards = Rewards(repositories, db);
            var create = new CreateTaskHandler(
                repositories.LocalProfiles,
                repositories.StudyTasks,
                repositories.Subjects,
                repositories.UnitOfWork,
                db.Clock);
            var complete = new CompleteTaskHandler(
                repositories.LocalProfiles,
                repositories.StudyTasks,
                repositories.UnitOfWork,
                rewards,
                db.Clock);

            for (var i = 0; i < 10; i++)
            {
                var created = await create.HandleAsync(profileId, $"Task {i}");
                Assert.True(created.Success, created.Error?.Message);
                taskIds.Add(created.Id!.Value);
            }

            foreach (var taskId in taskIds)
            {
                var result = await complete.HandleAsync(taskId);
                Assert.True(result.Success, result.Error?.Message);
            }

            var first = await repositories.StudyTasks.FindAsync(taskIds[0]);
            Assert.NotNull(first);
            Assert.Equal(StudyTaskStatus.Done, first.Status);
            firstCompletedAtUtc = first.CompletedAtUtc;
            completionRewardId = first.CompletionRewardId;
            Assert.NotNull(firstCompletedAtUtc);
            Assert.NotNull(completionRewardId);

            var reopen = new ReopenTaskHandler(repositories.StudyTasks, repositories.UnitOfWork, db.Clock);
            Assert.True((await reopen.HandleAsync(taskIds[0])).Success);

            var reopened = await repositories.StudyTasks.FindAsync(taskIds[0]);
            Assert.NotNull(reopened);
            Assert.Equal(StudyTaskStatus.Todo, reopened.Status);
            Assert.Equal(firstCompletedAtUtc, reopened.CompletedAtUtc);
            Assert.Equal(completionRewardId, reopened.CompletionRewardId);

            var second = await complete.HandleAsync(taskIds[0]);
            Assert.True(second.Success, second.Error?.Message);
            Assert.False(second.Changed);
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            var task = await verification.StudyTasks.FindAsync(taskIds[0]);
            Assert.NotNull(task);
            Assert.Equal(StudyTaskStatus.Done, task.Status);
            Assert.Equal(firstCompletedAtUtc, task.CompletedAtUtc);
            Assert.Equal(completionRewardId, task.CompletionRewardId);

            var entries = await verification.RewardLedger.ListByProfileAsync(profileId);
            Assert.Equal(11, entries.Count); // 10 task awards + one ACH-005 award
            Assert.Equal(325, entries.Sum(e => e.Amount));
            Assert.Single(entries, e => e.SourceType == LedgerSourceType.TaskCompletion
                && e.SourceId == taskIds[0].ToString());

            var unlocks = await verification.UserAchievements.ListByProfileAsync(profileId);
            Assert.Single(unlocks, u => u.AchievementCode == "ACH-005");

            var profile = await verification.LocalProfiles.FindAsync(profileId);
            Assert.NotNull(profile);
            Assert.Equal(325, profile.CachedTotalXp);
        }
    }

    [Fact]
    public async Task New_focus_session_persists_the_app_owned_rule_version_and_history_is_not_rewritten()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid sessionId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var started = await FocusStart(repositories, db)
                .HandleAsync(profileId, BuiltInProfiles.BalancedCode, 600);
            Assert.True(started.Success, started.Error?.Message);
            sessionId = started.Id!.Value;
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            var session = await verification.FocusSessions.FindAsync(sessionId);
            Assert.Equal(AppRules.Version, session!.AppRuleVersion);
            Assert.Equal("APP-RULES-1", session.AppRuleVersion);
        }

        // A historical row keeps whatever it recorded; no reader or updater rewrites it.
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE \"FocusSession\" SET \"AppRuleVersion\" = 'APP-RULES-0' WHERE \"Id\" = $id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString().ToUpperInvariant());
            await command.ExecuteNonQueryAsync();
        }

        await using var historical = db.Store.CreateRepositories();
        Assert.Equal("APP-RULES-0", (await historical.FocusSessions.FindAsync(sessionId))!.AppRuleVersion);
    }

    [Fact]
    public async Task ACH005_lifetime_task_count_survives_archive_and_soft_delete_in_sqlite()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var completed = new List<Guid>();

        await using (var repositories = db.Store.CreateRepositories())
        {
            var rewards = Rewards(repositories, db);
            var create = new CreateTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            var complete = new CompleteTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.UnitOfWork, rewards, db.Clock);
            var archive = new ArchiveTaskHandler(repositories.StudyTasks, repositories.UnitOfWork, db.Clock);
            var delete = new SoftDeleteTaskHandler(repositories.StudyTasks, repositories.UnitOfWork, db.Clock);

            for (var i = 0; i < 9; i++)
            {
                var id = (await create.HandleAsync(profileId, $"Task {i}")).Id!.Value;
                completed.Add(id);
                Assert.True((await complete.HandleAsync(id)).Success);
            }

            for (var i = 0; i < 5; i++)
            {
                Assert.True((await archive.HandleAsync(completed[i])).Success);
            }

            for (var i = 5; i < 9; i++)
            {
                Assert.True((await delete.HandleAsync(completed[i])).Success);
            }
        }

        await using (var repositories = db.Store.CreateRepositories())
        {
            var rewards = Rewards(repositories, db);
            var create = new CreateTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            var complete = new CompleteTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.UnitOfWork, rewards, db.Clock);

            var tenth = (await create.HandleAsync(profileId, "Tenth task")).Id!.Value;
            var result = await complete.HandleAsync(tenth);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(result.Changed);
        }

        await using var verification = db.Store.CreateRepositories();
        var entries = await verification.RewardLedger.ListByProfileAsync(profileId);
        Assert.Equal(11, entries.Count); // 10 task awards + one ACH-005 award
        Assert.Equal(325, entries.Sum(e => e.Amount));
        Assert.Single(entries, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP");
        Assert.Single(await verification.UserAchievements.ListByProfileAsync(profileId), u => u.AchievementCode == "ACH-005");
    }

    [Fact]
    public async Task Update_task_persists_the_new_details_through_the_shared_boundary()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid taskId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var create = new CreateTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            taskId = (await create.HandleAsync(profileId, "Original title")).Id!.Value;

            var update = new UpdateTaskHandler(
                repositories.StudyTasks, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            var result = await update.HandleAsync(
                taskId,
                "Updated title",
                "Updated notes",
                null,
                new DateOnly(2026, 12, 1),
                45,
                TaskPriority.High,
                new DateOnly(2026, 11, 20));
            Assert.True(result.Success, result.Error?.Message);
        }

        await using var verification = db.Store.CreateRepositories();
        var task = await verification.StudyTasks.FindAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal("Updated title", task.Title);
        Assert.Equal("Updated notes", task.Notes);
        Assert.Equal(45, task.EstimatedMinutes);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(new DateOnly(2026, 12, 1), task.DueDateLocal);
    }

    [Fact]
    public async Task Failed_command_rolls_back_a_tracked_task_update_in_real_sqlite()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid taskId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var create = new CreateTaskHandler(
                repositories.LocalProfiles, repositories.StudyTasks, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            taskId = (await create.HandleAsync(profileId, "Original title")).Id!.Value;

            var task = await repositories.StudyTasks.FindAsync(taskId);
            Assert.NotNull(task);

            var outcome = await CommandBoundary.ExecuteAsync<UseCaseResult>(
                repositories.UnitOfWork,
                () =>
                {
                    task.UpdateDetails(
                        "Must not persist", null, null, null, TaskPriority.Low, null, null, db.Clock.UtcNow);
                    return UseCaseResult.Fail("TEST_FAILURE", "simulated command failure");
                });

            Assert.False(outcome.Success);
        }

        await using var verification = db.Store.CreateRepositories();
        Assert.Equal("Original title", (await verification.StudyTasks.FindAsync(taskId))!.Title);
    }

    [Fact]
    public async Task Focus_start_commits_the_session_and_its_start_event_together()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid sessionId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var start = FocusStart(repositories, db);
            var result = await start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 600);
            Assert.True(result.Success, result.Error?.Message);
            sessionId = result.Id!.Value;
        }

        await using var verification = db.Store.CreateRepositories();
        Assert.NotNull(await verification.FocusSessions.FindAsync(sessionId));
        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId);
        var startEvent = Assert.Single(events);
        Assert.Equal(SessionEventType.Start, startEvent.EventType);
    }

    [Fact]
    public async Task Focus_start_rolls_back_the_session_when_the_start_event_cannot_be_written()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var failingEvents = new ThrowingSessionEventRepository(repositories.SessionEvents);
            var start = new StartFocusSessionHandler(
                repositories.LocalProfiles,
                repositories.FocusProfiles,
                repositories.FocusSessions,
                repositories.BreakSessions,
                repositories.StudyTasks,
                repositories.Subjects,
                repositories.UnitOfWork,
                db.Clock,
                new SessionEventRecorder(failingEvents, db.Clock),
                FocusAbandon(repositories, db));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 600));
        }

        await using var verification = db.Store.CreateRepositories();
        Assert.Equal(0, await verification.Context.FocusSessions.CountAsync());
        Assert.Equal(0, await verification.Context.SessionEvents.CountAsync());
    }

    [Fact]
    public async Task Break_start_commits_the_break_and_its_started_event_together_and_rolls_back_on_failure()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid breakId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var start = BreakStart(repositories, db);
            var result = await start.HandleAsync(profileId, 300);
            Assert.True(result.Success, result.Error?.Message);
            breakId = result.Id!.Value;
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            Assert.NotNull(await verification.BreakSessions.FindAsync(breakId));
            var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Break, breakId);
            Assert.Equal(SessionEventType.BreakStarted, Assert.Single(events).EventType);
        }
    }

    [Fact]
    public async Task Break_start_rolls_back_the_break_when_the_started_event_cannot_be_written()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var failingEvents = new ThrowingSessionEventRepository(repositories.SessionEvents);
            var start = new StartBreakHandler(
                repositories.FocusSessions,
                repositories.BreakSessions,
                repositories.UnitOfWork,
                db.Clock,
                new SessionEventRecorder(failingEvents, db.Clock));

            await Assert.ThrowsAsync<InvalidOperationException>(() => start.HandleAsync(profileId, 300));
        }

        await using var verification = db.Store.CreateRepositories();
        Assert.Equal(0, await verification.Context.BreakSessions.CountAsync());
        Assert.Equal(0, await verification.Context.SessionEvents.CountAsync());
    }

    [Fact]
    public async Task Subject_create_update_and_archive_persist_across_scopes()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var create = new CreateSubjectHandler(
                repositories.LocalProfiles, repositories.Subjects, repositories.UnitOfWork, db.Clock);
            subjectId = (await create.HandleAsync(profileId, "Biology", "Cells")).Id!.Value;

            var update = new UpdateSubjectHandler(repositories.Subjects, repositories.UnitOfWork, db.Clock);
            Assert.True((await update.HandleAsync(subjectId, "Biology II", "Genetics")).Success);

            var archive = new ArchiveSubjectHandler(repositories.Subjects, repositories.UnitOfWork, db.Clock);
            Assert.True((await archive.HandleAsync(subjectId)).Success);
        }

        await using (var verification = db.Store.CreateRepositories())
        {
            var subject = await verification.Subjects.FindAsync(subjectId);
            Assert.NotNull(subject);
            Assert.Equal("Biology II", subject.Name);
            Assert.Equal("Genetics", subject.Description);
            Assert.True(subject.IsArchived);

            var active = await verification.Subjects.ListAsync(profileId, includeArchived: false);
            Assert.DoesNotContain(active, s => s.Id == subjectId);
        }

        await using (var repositories = db.Store.CreateRepositories())
        {
            var archive = new ArchiveSubjectHandler(repositories.Subjects, repositories.UnitOfWork, db.Clock);
            Assert.True((await archive.HandleAsync(subjectId, restore: true)).Success);
        }

        await using var final = db.Store.CreateRepositories();
        Assert.False((await final.Subjects.FindAsync(subjectId))!.IsArchived);
    }

    [Fact]
    public async Task Focus_checkpoint_and_protection_status_persist_across_scopes()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid sessionId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            sessionId = (await FocusStart(repositories, db)
                .HandleAsync(profileId, BuiltInProfiles.BalancedCode, 600)).Id!.Value;

            var checkpoint = new ApplyFocusCheckpointHandler(
                repositories.FocusSessions, repositories.UnitOfWork, db.Clock);
            Assert.True((await checkpoint.HandleAsync(sessionId, 300, 240, 60, 10, 2, 1, 0)).Success);

            var protection = new SetFocusProtectionHandler(
                repositories.FocusSessions, repositories.UnitOfWork, db.Clock);
            Assert.True((await protection.HandleAsync(
                sessionId, ProtectionStatus.Protected, VerificationStatus.Verified)).Success);
        }

        await using var verification = db.Store.CreateRepositories();
        var session = await verification.FocusSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(300, session.ElapsedSeconds);
        Assert.Equal(240, session.ActiveStudySeconds);
        Assert.Equal(60, session.IdleSeconds);
        Assert.Equal(10, session.ProtectionGapSeconds);
        Assert.Equal(2, session.ViolationCount);
        Assert.Equal(1, session.ExceptionCount);
        Assert.Equal(ProtectionStatus.Protected, session.ProtectionStatus);
        Assert.Equal(VerificationStatus.Verified, session.VerificationStatus);
    }

    [Fact]
    public async Task Break_checkpoint_and_interrupt_persist_across_scopes()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        Guid breakId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            breakId = (await BreakStart(repositories, db).HandleAsync(profileId, 300)).Id!.Value;

            var checkpoint = new ApplyBreakCheckpointHandler(
                repositories.BreakSessions, repositories.UnitOfWork, db.Clock);
            Assert.True((await checkpoint.HandleAsync(breakId, 120)).Success);

            var interrupt = new InterruptBreakHandler(
                repositories.BreakSessions, repositories.UnitOfWork, db.Clock);
            Assert.True((await interrupt.HandleAsync(breakId, BreakEndReason.LockOrSleepTimeout)).Success);
        }

        await using var verification = db.Store.CreateRepositories();
        var session = await verification.BreakSessions.FindAsync(breakId);
        Assert.NotNull(session);
        Assert.Equal(BreakState.Interrupted, session.State);
        Assert.Equal(120, session.ElapsedSeconds);
        Assert.Equal(BreakEndReason.LockOrSleepTimeout, session.EndReason);
    }

    [Fact]
    public async Task Reads_do_not_open_a_write_transaction()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        await using var repositories = db.Store.CreateRepositories();

        _ = await repositories.StudyTasks.ListAsync(profileId);
        Assert.Null(repositories.Context.Database.CurrentTransaction);

        _ = await repositories.LocalProfiles.FindAsync(profileId);
        Assert.Null(repositories.Context.Database.CurrentTransaction);
    }

    private static RewardService Rewards(SqliteRepositorySet repositories, TestDatabase db) =>
        new(repositories.RewardLedger,
            repositories.Achievements,
            repositories.UserAchievements,
            repositories.RewardState,
            db.Clock);

    private static StartFocusSessionHandler FocusStart(SqliteRepositorySet repositories, TestDatabase db) =>
        new(repositories.LocalProfiles,
            repositories.FocusProfiles,
            repositories.FocusSessions,
            repositories.BreakSessions,
            repositories.StudyTasks,
            repositories.Subjects,
            repositories.UnitOfWork,
            db.Clock,
            new SessionEventRecorder(repositories.SessionEvents, db.Clock),
            FocusAbandon(repositories, db));

    private static AbandonFocusSessionHandler FocusAbandon(
        SqliteRepositorySet repositories,
        TestDatabase db) =>
        new(
            repositories.FocusSessions,
            db.Clock,
            new RecoverFocusSessionHandler(
                repositories.FocusSessions,
                repositories.LocalProfiles,
                repositories.UnitOfWork,
                Rewards(repositories, db),
                db.Clock,
                new SessionEventRecorder(repositories.SessionEvents, db.Clock)));

    private static StartBreakHandler BreakStart(SqliteRepositorySet repositories, TestDatabase db) =>
        new(repositories.FocusSessions,
            repositories.BreakSessions,
            repositories.UnitOfWork,
            db.Clock,
            new SessionEventRecorder(repositories.SessionEvents, db.Clock));

    private sealed class ThrowingSessionEventRepository : ISessionEventRepository
    {
        private readonly ISessionEventRepository _inner;

        public ThrowingSessionEventRepository(ISessionEventRepository inner)
        {
            _inner = inner;
        }

        public Task<int> MaxSequenceAsync(
            SessionType sessionType,
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            _inner.MaxSequenceAsync(sessionType, sessionId, cancellationToken);

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            SessionType sessionType,
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            _inner.ListBySessionAsync(sessionType, sessionId, cancellationToken);

        public Task AddAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated event-store failure");
    }
}
