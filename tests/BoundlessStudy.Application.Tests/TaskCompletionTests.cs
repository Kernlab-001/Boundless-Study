using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Task completion reward behavior (TV-09, TV-18; ECONOMY-1 §8.5, §8.10, §8.11).
/// </summary>
public class TaskCompletionTests
{
    [Fact]
    public async Task TV09_first_completion_awards_25_once_and_the_second_is_no_change()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("Read chapter 4");

        var first = await harness.CompleteTask.HandleAsync(taskId);
        var second = await harness.CompleteTask.HandleAsync(taskId);

        Assert.True(first.Success);
        Assert.True(first.Changed);
        Assert.Equal(25, first.TotalXpAfter);
        Assert.Equal(1, first.LevelBefore);
        Assert.Equal(1, first.LevelAfter);

        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(25, second.TotalXpAfter);

        Assert.Single(harness.Ledger.All);
        Assert.Equal($"TaskCompletion:{taskId}:XP", harness.Ledger.All[0].IdempotencyKey);
        Assert.Equal(25, harness.Ledger.All[0].Amount);
        Assert.Equal(LedgerSourceType.TaskCompletion, harness.Ledger.All[0].SourceType);
    }

    [Fact]
    public async Task TV18_reopen_and_recomplete_awards_once()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("Lab report");

        var first = await harness.CompleteTask.HandleAsync(taskId);
        var afterFirst = await harness.Tasks.FindAsync(taskId);
        Assert.True(first.Success);
        Assert.True(first.Changed);
        Assert.Equal(StudyTaskStatus.Done, afterFirst!.Status);
        Assert.NotNull(afterFirst.CompletedAtUtc);
        var rewardId = afterFirst.CompletionRewardId;
        Assert.NotNull(rewardId);
        Assert.Single(harness.Ledger.All);

        var reopen = await harness.ReopenTask.HandleAsync(taskId);
        var afterReopen = await harness.Tasks.FindAsync(taskId);
        Assert.True(reopen.Success);
        Assert.Equal(StudyTaskStatus.Todo, afterReopen!.Status);
        Assert.Equal(rewardId, afterReopen.CompletionRewardId);

        var second = await harness.CompleteTask.HandleAsync(taskId);
        var afterSecond = await harness.Tasks.FindAsync(taskId);

        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(StudyTaskStatus.Done, afterSecond!.Status);
        Assert.Equal(rewardId, afterSecond.CompletionRewardId);
        Assert.Single(harness.Ledger.All);
        Assert.Equal(25, harness.Ledger.All.Sum(e => e.Amount));
        Assert.Empty(harness.UserAchievements.All);
    }

    [Fact]
    public async Task Reopening_a_task_never_reunlocks_ACH005_or_adds_reward_xp()
    {
        var harness = new TestHarness();
        var taskIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task {i}");
            taskIds.Add(id);
            await harness.CompleteTask.HandleAsync(id);
        }

        var totalBefore = await harness.Ledger.SumXpAsync(harness.Profile.Id);
        var ledgerCountBefore = harness.Ledger.All.Count;
        Assert.Single(harness.UserAchievements.All, a => a.AchievementCode == "ACH-005");

        await harness.ReopenTask.HandleAsync(taskIds[0]);
        var second = await harness.CompleteTask.HandleAsync(taskIds[0]);

        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(StudyTaskStatus.Done, (await harness.Tasks.FindAsync(taskIds[0]))!.Status);
        Assert.Equal(totalBefore, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Equal(ledgerCountBefore, harness.Ledger.All.Count);
        Assert.Single(harness.UserAchievements.All, a => a.AchievementCode == "ACH-005");
    }

    [Fact]
    public async Task Task_xp_does_not_consume_the_session_daily_cap()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId();

        await harness.CompleteTask.HandleAsync(taskId);

        var sessionXpToday = await harness.Ledger.SumSessionXpForLocalDateAsync(harness.Profile.Id, harness.Clock.Today);
        Assert.Equal(0, sessionXpToday);
        Assert.Equal(25, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task Tenth_task_completion_unlocks_ACH005_and_awards_75()
    {
        var harness = new TestHarness();
        for (var i = 0; i < 10; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task {i}");
            var result = await harness.CompleteTask.HandleAsync(id);
            Assert.True(result.Success);
        }

        // 10 × 25 task XP + 75 achievement XP.
        Assert.Equal(325, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP" && e.Amount == 75);
        Assert.NotNull(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005"));
        Assert.Single(harness.UserAchievements.All, a => a.AchievementCode == "ACH-005");
    }

    [Fact]
    public async Task Achievement_award_is_duplicate_safe_on_repeated_requests()
    {
        var harness = new TestHarness();
        for (var i = 0; i < 10; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task {i}");
            await harness.CompleteTask.HandleAsync(id);
        }

        // Re-completing tasks must not re-unlock ACH-005 or add XP.
        var totalBefore = await harness.Ledger.SumXpAsync(harness.Profile.Id);
        foreach (var task in harness.Tasks.All)
        {
            await harness.CompleteTask.HandleAsync(task.Id);
        }

        Assert.Equal(totalBefore, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Single(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP");
    }

    [Fact]
    public async Task ACH005_counts_lifetime_completions_of_archived_and_soft_deleted_tasks()
    {
        var harness = new TestHarness();
        var completed = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task {i}");
            completed.Add(id);
            Assert.True((await harness.CompleteTask.HandleAsync(id)).Success);
        }

        // Five completed tasks are archived and four are soft-deleted. Their permanent
        // CompletedAtUtc markers must still count toward the 10-task achievement.
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await harness.ArchiveTask.HandleAsync(completed[i])).Success);
        }

        for (var i = 5; i < 9; i++)
        {
            Assert.True((await harness.DeleteTask.HandleAsync(completed[i])).Success);
        }

        Assert.Null(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005"));

        var tenth = harness.CreateTaskAndGetId("Tenth task");
        var result = await harness.CompleteTask.HandleAsync(tenth);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.NotNull(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005"));
        Assert.Single(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP");
        Assert.Equal(325, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }

    [Fact]
    public async Task ACH005_history_survives_archive_delete_and_reopen_without_a_second_award()
    {
        var harness = new TestHarness();
        var taskIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task {i}");
            taskIds.Add(id);
            await harness.CompleteTask.HandleAsync(id);
        }

        var totalBefore = await harness.Ledger.SumXpAsync(harness.Profile.Id);
        var ledgerCountBefore = harness.Ledger.All.Count;
        var achievementBefore = await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005");

        await harness.ArchiveTask.HandleAsync(taskIds[0]);
        await harness.DeleteTask.HandleAsync(taskIds[1]);
        Assert.True((await harness.ReopenTask.HandleAsync(taskIds[2])).Success);
        Assert.True((await harness.CompleteTask.HandleAsync(taskIds[2])).Success);
        Assert.True((await harness.CompleteTask.HandleAsync(taskIds[3])).Success);

        Assert.Equal(totalBefore, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Equal(ledgerCountBefore, harness.Ledger.All.Count);
        Assert.Equal(achievementBefore!.Id, (await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005"))!.Id);
        Assert.Single(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP");
    }

    [Fact]
    public async Task Deleted_task_cannot_be_completed()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId();
        await harness.DeleteTask.HandleAsync(taskId);

        var result = await harness.CompleteTask.HandleAsync(taskId);

        Assert.False(result.Success);
        Assert.Equal(UseCaseErrorCodes.TaskDeleted, result.Error!.Code);
        Assert.Empty(harness.Ledger.All);
    }

    [Fact]
    public async Task Missing_task_returns_a_stable_error()
    {
        var harness = new TestHarness();
        var result = await harness.CompleteTask.HandleAsync(Guid.NewGuid());
        Assert.False(result.Success);
        Assert.Equal(UseCaseErrorCodes.TaskNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task Completion_reward_links_the_ledger_entry_for_audit()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId();
        await harness.CompleteTask.HandleAsync(taskId);

        var task = await harness.Tasks.FindAsync(taskId);
        Assert.NotNull(task!.CompletionRewardId);
        Assert.Equal(harness.Ledger.All.Single().Id, task.CompletionRewardId);
    }

    [Fact]
    public async Task Task_completion_and_achievement_commit_in_one_transaction()
    {
        var harness = new TestHarness();
        var taskIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            taskIds.Add(harness.CreateTaskAndGetId($"Task {i}"));
        }

        harness.UnitOfWork.ResetCounts();
        foreach (var taskId in taskIds)
        {
            await harness.CompleteTask.HandleAsync(taskId);
        }

        // One commit for each completing call, including the call that unlocked ACH-005.
        Assert.Equal(10, harness.UnitOfWork.BeginCount);
        Assert.Equal(10, harness.UnitOfWork.CommitCount);
        Assert.Equal(0, harness.UnitOfWork.RollbackCount);
        Assert.Equal(325, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }
}
