using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class RewardStateTests
{
    [Fact]
    public async Task Derived_reward_counts_follow_the_approved_terminal_and_qualified_rules()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);
        var now = db.Clock.UtcNow;

        var balanced = await AddCompletedAsync(
            repositories, profile.Id, BuiltInProfiles.BalancedCode, activeSeconds: 600, now);
        var strict1 = await AddCompletedAsync(
            repositories, profile.Id, BuiltInProfiles.StrictCode, activeSeconds: 600, now.AddMinutes(1));
        var strict2 = await AddCompletedAsync(
            repositories, profile.Id, BuiltInProfiles.StrictCode, activeSeconds: 700, now.AddMinutes(2));
        var almost = await AddCompletedAsync(
            repositories, profile.Id, BuiltInProfiles.BalancedCode, activeSeconds: 599, now.AddMinutes(3));

        var endedEarly = FocusSession.Start(
            profile.Id, BuiltInProfiles.BalancedCode, 600, now.AddMinutes(4), EconomyRules.Version);
        endedEarly.EndEarly(now.AddMinutes(9), 300, 300, 0, 0, FocusEndReason.UserEnded);
        await repositories.FocusSessions.AddAsync(endedEarly);

        var abandoned = FocusSession.Start(
            profile.Id, BuiltInProfiles.BalancedCode, 600, now.AddMinutes(10), EconomyRules.Version);
        abandoned.ApplyCheckpoint(now.AddMinutes(12), 120, 120, 0, 0, 0, 0, 0);
        abandoned.MarkRecoverableInterrupted(now.AddMinutes(12), FocusEndReason.AgentCrash);
        abandoned.Abandon(now.AddMinutes(13), FocusEndReason.AgentCrash);
        await repositories.FocusSessions.AddAsync(abandoned);

        var active = FocusSession.Start(
            profile.Id, BuiltInProfiles.BalancedCode, 600, now.AddMinutes(14), EconomyRules.Version);
        active.ApplyCheckpoint(now.AddMinutes(54), 2_400, 2_400, 0, 0, 0, 0, 0);
        await repositories.FocusSessions.AddAsync(active);

        Assert.Equal(3, await repositories.RewardState.GetQualifiedSessionCountAsync(profile.Id));
        Assert.Equal(2, await repositories.RewardState.GetStrictQualifiedSessionCountAsync(profile.Id));
        Assert.Equal(48, await repositories.RewardState.GetTotalActiveStudyMinutesAsync(profile.Id));

        var task1 = StudyTask.Create(profile.Id, "Task 1", now);
        var task2 = StudyTask.Create(profile.Id, "Task 2", now);
        var task3 = StudyTask.Create(profile.Id, "Task 3", now);
        await repositories.StudyTasks.AddAsync(task1);
        await repositories.StudyTasks.AddAsync(task2);
        await repositories.StudyTasks.AddAsync(task3);
        Assert.True(task1.Complete(now));
        Assert.True(task2.Complete(now));
        await repositories.Context.SaveChangesAsync();

        Assert.Equal(2, await repositories.RewardState.GetCompletedTaskCountAsync(profile.Id));

        // A completion remains counted after the task is archived, soft-deleted,
        // or reopened; only the first completion is historical and permanent.
        task1.Archive(now);
        task2.SoftDelete(now);
        task3.Complete(now);
        task3.Reopen(now);
        await repositories.Context.SaveChangesAsync();

        Assert.Equal(3, await repositories.RewardState.GetCompletedTaskCountAsync(profile.Id));

        await AddEventAsync(repositories, balanced, 1, SessionEventType.Completed, new DateOnly(2026, 11, 10), now);
        await AddEventAsync(repositories, balanced, 2, SessionEventType.RewardCreated, new DateOnly(2026, 11, 9), now);
        await AddEventAsync(repositories, balanced, 3, SessionEventType.Completed, new DateOnly(2026, 11, 10), now);
        await AddEventAsync(repositories, strict1, 1, SessionEventType.RewardCreated, new DateOnly(2026, 11, 11), now);
        await AddEventAsync(repositories, strict2, 1, SessionEventType.Completed, new DateOnly(2026, 11, 12), now);
        await AddEventAsync(repositories, almost, 1, SessionEventType.Completed, new DateOnly(2026, 11, 13), now);
        await AddEventAsync(repositories, endedEarly, 1, SessionEventType.Completed, new DateOnly(2026, 11, 14), now);

        var dates = await repositories.RewardState.ListQualifiedLocalDatesAsync(profile.Id);

        Assert.Equal(
            new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12) },
            dates);
    }

    private static async Task<FocusSession> AddCompletedAsync(
        SqliteRepositorySet repositories,
        Guid profileId,
        string profileCode,
        int activeSeconds,
        DateTime startedAtUtc)
    {
        var session = FocusSession.Start(
            profileId, profileCode, 600, startedAtUtc, EconomyRules.Version);
        var elapsedSeconds = Math.Max(600, activeSeconds);
        var idleSeconds = elapsedSeconds - activeSeconds;
        session.Complete(startedAtUtc.AddMinutes(10), elapsedSeconds, activeSeconds, idleSeconds, 0);
        await repositories.FocusSessions.AddAsync(session);
        return session;
    }

    private static async Task AddEventAsync(
        SqliteRepositorySet repositories,
        FocusSession session,
        int sequence,
        SessionEventType eventType,
        DateOnly localDate,
        DateTime occurredAtUtc)
    {
        var sessionEvent = SessionEvent.Create(
            SessionType.Focus,
            session.Id,
            sequence,
            eventType,
            occurredAtUtc,
            localDate,
            0,
            string.Empty,
            occurredAtUtc);
        await repositories.SessionEvents.AddAsync(sessionEvent);
    }
}
