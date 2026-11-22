using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Functional Subject and Task workflow tests (BS-STU-001, BS-STU-002, BS-STU-004, BS-STU-007,
/// TV-09, TV-18, §8.5, §9.2, ACH-002, ACH-005).
/// </summary>
public class SubjectAndTaskWorkflowTests
{
    // =========================================================================
    // 1. Subject Workflows: Creation, Validation, Renaming, Archiving, Restoring, Duplicate Warning
    // =========================================================================

    [Fact]
    public async Task Subject_creation_with_valid_name_and_description_succeeds()
    {
        var harness = new TestHarness();

        var result = await harness.CreateSubject.HandleAsync(
            harness.Profile.Id,
            "Mathematics",
            "Calculus and Linear Algebra");

        Assert.True(result.Success);
        Assert.NotNull(result.Id);

        var subject = await harness.Subjects.FindAsync(result.Id.Value);
        Assert.NotNull(subject);
        Assert.Equal("Mathematics", subject.Name);
        Assert.Equal("Calculus and Linear Algebra", subject.Description);
        Assert.False(subject.IsArchived);
        Assert.Equal(harness.Profile.Id, subject.LocalProfileId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Subject_creation_rejects_empty_or_whitespace_name(string invalidName)
    {
        var harness = new TestHarness();

        var result = await harness.CreateSubject.HandleAsync(
            harness.Profile.Id,
            invalidName,
            "Description");

        Assert.False(result.Success);
        Assert.Equal("SUBJECT_NAME_REQUIRED", result.Error?.Code);
    }

    [Fact]
    public async Task Subject_creation_rejects_name_exceeding_100_characters()
    {
        var harness = new TestHarness();
        var longName = new string('A', 101);

        var result = await harness.CreateSubject.HandleAsync(
            harness.Profile.Id,
            longName,
            "Description");

        Assert.False(result.Success);
        Assert.Equal("SUBJECT_NAME_TOO_LONG", result.Error?.Code);
    }

    [Fact]
    public async Task Duplicate_subject_names_are_permitted_in_domain_and_flagged_for_warning()
    {
        // Per BS-STU-001: Duplicates are allowed; the UI warns.
        var harness = new TestHarness();

        var first = await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Physics", "Mechanics");
        var second = await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Physics", "Electromagnetism");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.Id, second.Id);

        var list = await harness.ListSubjects.HandleAsync(harness.Profile.Id);
        Assert.Equal(2, list.Count(s => s.Name.Equals("Physics", StringComparison.OrdinalIgnoreCase)));

        // Warning detection logic: verifies client-side / service duplicate detection
        var client = new InMemoryAgentClient();
        await client.CreateSubjectAsync(new CreateSubjectRequest("Physics", "First"));
        var existing = await client.GetSubjectsAsync();
        var isDuplicate = existing.Any(s => s.Name.Equals("Physics", StringComparison.OrdinalIgnoreCase));
        Assert.True(isDuplicate);
    }

    [Fact]
    public async Task Subject_renaming_and_description_update_persists_cleanly()
    {
        var harness = new TestHarness();
        var createResult = await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Chemistry", "Intro");
        var subjectId = createResult.Id!.Value;

        var updateResult = await harness.UpdateSubject.HandleAsync(
            subjectId,
            "Organic Chemistry",
            "Advanced Reactions");

        Assert.True(updateResult.Success);

        var subject = await harness.Subjects.FindAsync(subjectId);
        Assert.NotNull(subject);
        Assert.Equal("Organic Chemistry", subject.Name);
        Assert.Equal("Advanced Reactions", subject.Description);
    }

    [Fact]
    public async Task Subject_archiving_and_restoring_toggles_archived_state()
    {
        var harness = new TestHarness();
        var createResult = await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Biology", null);
        var subjectId = createResult.Id!.Value;

        // Archive
        var archiveResult = await harness.ArchiveSubject.HandleAsync(subjectId, restore: false);
        Assert.True(archiveResult.Success);

        var archived = await harness.Subjects.FindAsync(subjectId);
        Assert.True(archived!.IsArchived);

        var activeOnly = await harness.ListSubjects.HandleAsync(harness.Profile.Id, includeArchived: false);
        Assert.DoesNotContain(activeOnly, s => s.Id == subjectId);

        var allSubjects = await harness.ListSubjects.HandleAsync(harness.Profile.Id, includeArchived: true);
        Assert.Contains(allSubjects, s => s.Id == subjectId);

        // Restore
        var restoreResult = await harness.ArchiveSubject.HandleAsync(subjectId, restore: true);
        Assert.True(restoreResult.Success);

        var restored = await harness.Subjects.FindAsync(subjectId);
        Assert.False(restored!.IsArchived);

        activeOnly = await harness.ListSubjects.HandleAsync(harness.Profile.Id, includeArchived: false);
        Assert.Contains(activeOnly, s => s.Id == subjectId);
    }

    // =========================================================================
    // 2. Task Workflows: Creation, Updating, Subject Assignment, Start
    // =========================================================================

    [Fact]
    public async Task Task_creation_and_subject_assignment_persists_all_fields()
    {
        var harness = new TestHarness();
        var subjectResult = await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Literature", "World Lit");
        var subjectId = subjectResult.Id!.Value;

        var dueDate = new DateOnly(2026, 12, 1);
        var plannedDate = new DateOnly(2026, 11, 25);

        var taskResult = await harness.CreateTask.HandleAsync(
            harness.Profile.Id,
            "Read Hamlet Act 1",
            notes: "Focus on soliloquies",
            subjectId: subjectId,
            dueDateLocal: dueDate,
            estimatedMinutes: 45,
            priority: TaskPriority.High,
            plannedForLocalDate: plannedDate);

        Assert.True(taskResult.Success);
        Assert.NotNull(taskResult.Id);

        var task = await harness.Tasks.FindAsync(taskResult.Id.Value);
        Assert.NotNull(task);
        Assert.Equal("Read Hamlet Act 1", task.Title);
        Assert.Equal("Focus on soliloquies", task.Notes);
        Assert.Equal(subjectId, task.SubjectId);
        Assert.Equal(dueDate, task.DueDateLocal);
        Assert.Equal(45, task.EstimatedMinutes);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(plannedDate, task.PlannedForLocalDate);
        Assert.Equal(StudyTaskStatus.Todo, task.Status);
        Assert.False(task.IsDeleted);
        Assert.Null(task.CompletedAtUtc);

        var list = await harness.ListTasks.HandleAsync(harness.Profile.Id);
        var summary = list.FirstOrDefault(t => t.Id == task.Id);
        Assert.NotNull(summary);
        Assert.Equal("Literature", summary.SubjectName);
    }

    [Fact]
    public async Task Task_creation_with_nonexistent_subject_fails()
    {
        var harness = new TestHarness();
        var missingSubjectId = Guid.NewGuid();

        var result = await harness.CreateTask.HandleAsync(
            harness.Profile.Id,
            "Task with bad subject",
            subjectId: missingSubjectId);

        Assert.False(result.Success);
        Assert.Equal(UseCaseErrorCodes.SubjectNotFound, result.Error?.Code);
    }

    [Fact]
    public async Task Task_updating_modifies_details_and_subject_reassignment()
    {
        var harness = new TestHarness();
        var sub1 = (await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Sub1", null)).Id!.Value;
        var sub2 = (await harness.CreateSubject.HandleAsync(harness.Profile.Id, "Sub2", null)).Id!.Value;

        var taskId = (await harness.CreateTask.HandleAsync(harness.Profile.Id, "Initial Title", subjectId: sub1)).Id!.Value;

        var updateResult = await harness.UpdateTask.HandleAsync(
            taskId,
            "Updated Title",
            "Updated Notes",
            subjectId: sub2,
            dueDateLocal: new DateOnly(2026, 12, 15),
            estimatedMinutes: 60,
            priority: TaskPriority.High,
            plannedForLocalDate: new DateOnly(2026, 12, 10));

        Assert.True(updateResult.Success);

        var task = await harness.Tasks.FindAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal("Updated Title", task.Title);
        Assert.Equal("Updated Notes", task.Notes);
        Assert.Equal(sub2, task.SubjectId);
        Assert.Equal(TaskPriority.High, task.Priority);
        Assert.Equal(60, task.EstimatedMinutes);
    }

    [Fact]
    public async Task StartTask_transitions_Todo_task_to_InProgress()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("Essay draft");

        var result = await harness.StartTask.HandleAsync(taskId);
        Assert.True(result.Success);

        var task = await harness.Tasks.FindAsync(taskId);
        Assert.Equal(StudyTaskStatus.InProgress, task!.Status);
    }

    // =========================================================================
    // 3. Task Completion: Idempotent XP Award (TV-09)
    // =========================================================================

    [Fact]
    public async Task Task_completion_awards_XP_once_and_repeated_completion_produces_no_extra_XP()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("Solve problem set 3");

        var firstCompletion = await harness.CompleteTask.HandleAsync(taskId);
        Assert.True(firstCompletion.Success);
        Assert.True(firstCompletion.Changed);
        Assert.Equal(EconomyRules.TaskCompletionXp, firstCompletion.TotalXpAfter);

        var taskAfterFirst = await harness.Tasks.FindAsync(taskId);
        Assert.NotNull(taskAfterFirst);
        Assert.Equal(StudyTaskStatus.Done, taskAfterFirst.Status);
        Assert.NotNull(taskAfterFirst.CompletedAtUtc);
        Assert.NotNull(taskAfterFirst.CompletionRewardId);

        var firstCompletionTimestamp = taskAfterFirst.CompletedAtUtc;
        var firstRewardId = taskAfterFirst.CompletionRewardId;

        // Second completion (idempotent no-op)
        var secondCompletion = await harness.CompleteTask.HandleAsync(taskId);
        Assert.True(secondCompletion.Success);
        Assert.False(secondCompletion.Changed);
        Assert.Equal(EconomyRules.TaskCompletionXp, secondCompletion.TotalXpAfter);

        // Verify ledger has exactly one entry
        Assert.Single(harness.Ledger.All);
        Assert.Equal($"TaskCompletion:{taskId}:XP", harness.Ledger.All[0].IdempotencyKey);
        Assert.Equal(EconomyRules.TaskCompletionXp, harness.Ledger.All[0].Amount);

        // Verify timestamps unchanged
        var taskAfterSecond = await harness.Tasks.FindAsync(taskId);
        Assert.Equal(firstCompletionTimestamp, taskAfterSecond!.CompletedAtUtc);
        Assert.Equal(firstRewardId, taskAfterSecond.CompletionRewardId);
    }

    // =========================================================================
    // 4. Task Reopening: Invariant Preservation (TV-18, §8.5)
    // =========================================================================

    [Fact]
    public async Task Task_reopening_preserves_original_completion_timestamp_and_reward()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("History Paper");

        // Complete initially
        var completeResult = await harness.CompleteTask.HandleAsync(taskId);
        Assert.True(completeResult.Success);
        Assert.True(completeResult.Changed);

        var taskCompleted = await harness.Tasks.FindAsync(taskId);
        var originalCompletedAt = taskCompleted!.CompletedAtUtc;
        var originalRewardId = taskCompleted.CompletionRewardId;
        Assert.NotNull(originalCompletedAt);
        Assert.NotNull(originalRewardId);

        // Reopen task
        var reopenResult = await harness.ReopenTask.HandleAsync(taskId);
        Assert.True(reopenResult.Success);

        var taskReopened = await harness.Tasks.FindAsync(taskId);
        Assert.NotNull(taskReopened);
        Assert.Equal(StudyTaskStatus.Todo, taskReopened.Status);

        // Reward invariants (§8.5, TV-18): CompletedAtUtc and CompletionRewardId are preserved!
        Assert.Equal(originalCompletedAt, taskReopened.CompletedAtUtc);
        Assert.Equal(originalRewardId, taskReopened.CompletionRewardId);

        // Start task again
        var startResult = await harness.StartTask.HandleAsync(taskId);
        Assert.True(startResult.Success);
        var taskStarted = await harness.Tasks.FindAsync(taskId);
        Assert.Equal(StudyTaskStatus.InProgress, taskStarted!.Status);

        // Re-complete task
        var recompleteResult = await harness.CompleteTask.HandleAsync(taskId);
        Assert.True(recompleteResult.Success);
        Assert.False(recompleteResult.Changed); // No additional XP awarded

        var taskFinal = await harness.Tasks.FindAsync(taskId);
        Assert.Equal(StudyTaskStatus.Done, taskFinal!.Status);
        Assert.Equal(originalRewardId, taskFinal.CompletionRewardId);

        // Ledger must still have only 1 award for this task
        Assert.Single(harness.Ledger.All);
        Assert.Equal(EconomyRules.TaskCompletionXp, await harness.Ledger.SumXpAsync(harness.Profile.Id));
    }

    // =========================================================================
    // 5. Soft Delete & Archiving: History Contribution Preserved (ACH-002, ACH-005)
    // =========================================================================

    [Fact]
    public async Task Task_archiving_and_soft_delete_preserves_ACH005_task_finisher_milestone()
    {
        var harness = new TestHarness();
        var tasks = new List<Guid>();

        // Create and complete 9 tasks
        for (int i = 0; i < 9; i++)
        {
            var id = harness.CreateTaskAndGetId($"Task #{i + 1}");
            tasks.Add(id);
            var res = await harness.CompleteTask.HandleAsync(id);
            Assert.True(res.Success);
        }

        // Archive 4 completed tasks
        for (int i = 0; i < 4; i++)
        {
            var archiveRes = await harness.ArchiveTask.HandleAsync(tasks[i]);
            Assert.True(archiveRes.Success);
        }

        // Soft-delete 4 completed tasks
        for (int i = 4; i < 8; i++)
        {
            var deleteRes = await harness.DeleteTask.HandleAsync(tasks[i]);
            Assert.True(deleteRes.Success);
        }

        // Reopen and re-complete task 8
        Assert.True((await harness.ReopenTask.HandleAsync(tasks[8])).Success);
        Assert.True((await harness.CompleteTask.HandleAsync(tasks[8])).Success);

        // ACH-005 should NOT be unlocked yet (at 9 unique completed tasks)
        Assert.Null(await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005"));

        // Complete the 10th task
        var tenthTaskId = harness.CreateTaskAndGetId("Tenth Task");
        var tenthResult = await harness.CompleteTask.HandleAsync(tenthTaskId);

        Assert.True(tenthResult.Success);
        Assert.True(tenthResult.Changed);

        // Invariant: Completed tasks that were archived or soft-deleted still count toward ACH-005!
        var ach005 = await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-005");
        Assert.NotNull(ach005);
        Assert.Equal("ACH-005", ach005.AchievementCode);

        // 10 tasks × 25 XP + 75 XP achievement award = 325 XP
        Assert.Equal(325, await harness.Ledger.SumXpAsync(harness.Profile.Id));
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-005:XP");
    }

    [Fact]
    public async Task Focus_session_linked_to_archived_or_deleted_task_still_contributes_to_ACH002()
    {
        var harness = new TestHarness();
        var taskId = harness.CreateTaskAndGetId("Project research");

        // Run a 30-minute focus session linked to the task
        var session1Id = harness.StartSessionAndGetId("Balanced", plannedMinutes: 30, taskId: taskId);
        var outcome1 = harness.Complete(session1Id, activeMinutes: 30, plannedMinutes: 30);
        Assert.True(outcome1.Success);

        // Archive the task
        Assert.True((await harness.ArchiveTask.HandleAsync(taskId)).Success);

        // Run another 30-minute focus session linked to the archived task
        var session2Id = harness.StartSessionAndGetId("Balanced", plannedMinutes: 30, taskId: taskId);
        var outcome2 = harness.Complete(session2Id, activeMinutes: 30, plannedMinutes: 30);
        Assert.True(outcome2.Success);

        // Soft-delete the task
        Assert.True((await harness.DeleteTask.HandleAsync(taskId)).Success);

        // 60 total verified active study minutes accumulated: ACH-002 (One Hour) must unlock!
        var ach002 = await harness.UserAchievements.FindAsync(harness.Profile.Id, "ACH-002");
        Assert.NotNull(ach002);
        Assert.Equal("ACH-002", ach002.AchievementCode);
        Assert.Contains(harness.Ledger.All, e => e.IdempotencyKey == "AchievementUnlock:ACH-002:XP");
    }

    // =========================================================================
    // 6. IAgentClient and InMemoryAgentClient Contract Invariant Verification
    // =========================================================================

    [Fact]
    public async Task InMemoryAgentClient_faithfully_enforces_all_subject_and_task_invariants()
    {
        IAgentClient client = new InMemoryAgentClient();

        // 1. Create subject
        var subject = await client.CreateSubjectAsync(new CreateSubjectRequest("Neuroscience", "Brain and behavior"));
        Assert.Equal("Neuroscience", subject.Name);
        Assert.False(subject.IsArchived);

        // 2. Create task linked to subject
        var task = await client.CreateTaskAsync(new CreateTaskRequest(
            "Synapse review",
            "Diagram axon terminal",
            subject.Id,
            new DateOnly(2026, 12, 20),
            40,
            "High",
            new DateOnly(2026, 12, 18)));

        Assert.Equal("Synapse review", task.Title);
        Assert.Equal(subject.Id, task.SubjectId);
        Assert.Equal("Neuroscience", task.SubjectName);
        Assert.Equal("Todo", task.Status);

        // 3. Start task
        var started = await client.StartTaskAsync(new StartTaskRequest(task.Id));
        Assert.Equal("InProgress", started.Status);

        // 4. Complete task
        var completed = await client.CompleteTaskAsync(new CompleteTaskRequest(task.Id));
        Assert.Equal("Done", completed.Status);
        Assert.NotNull(completed.CompletedAtUtc);
        var originalCompletedAt = completed.CompletedAtUtc;

        // 5. Reopen task
        var reopened = await client.ReopenTaskAsync(new ReopenTaskRequest(task.Id));
        Assert.Equal("Todo", reopened.Status);
        Assert.Equal(originalCompletedAt, reopened.CompletedAtUtc); // Invariant preserved!

        // 6. Archive task
        var archived = await client.ArchiveTaskAsync(new ArchiveTaskRequest(task.Id, Restore: false));
        Assert.True(archived.IsArchived);
        Assert.Equal("Archived", archived.Status);

        // 7. Restore task
        var restored = await client.ArchiveTaskAsync(new ArchiveTaskRequest(task.Id, Restore: true));
        Assert.False(restored.IsArchived);

        // 8. Soft-delete task
        var deleted = await client.DeleteTaskAsync(new SoftDeleteTaskRequest(task.Id));
        Assert.True(deleted.IsDeleted);

        // Listing tasks excludes deleted tasks
        var remaining = await client.GetTasksAsync();
        Assert.DoesNotContain(remaining, t => t.Id == task.Id);
    }
}
