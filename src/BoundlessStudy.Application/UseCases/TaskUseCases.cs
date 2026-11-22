using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>Creates a task (BS-STU-002).</summary>
public sealed class CreateTaskHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IStudyTaskRepository _tasks;
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public CreateTaskHandler(
        ILocalProfileRepository profiles,
        IStudyTaskRepository tasks,
        ISubjectRepository subjects,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _profiles = profiles;
        _tasks = tasks;
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<EntityResult> HandleAsync(
        Guid localProfileId,
        string title,
        string? notes = null,
        Guid? subjectId = null,
        DateOnly? dueDateLocal = null,
        int? estimatedMinutes = null,
        TaskPriority priority = TaskPriority.Normal,
        DateOnly? plannedForLocalDate = null,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        if (subjectId is { } sid && await _subjects.FindAsync(sid, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.SubjectNotFound, "The linked subject does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var task = StudyTask.Create(
                        localProfileId,
                        title,
                        _clock.UtcNow,
                        subjectId,
                        notes,
                        dueDateLocal,
                        estimatedMinutes,
                        priority,
                        plannedForLocalDate);
                    await _tasks.AddAsync(task, ct);
                    return EntityResult.Ok(task.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Edits task fields (BS-STU-002); all fields validate and persist.</summary>
public sealed class UpdateTaskHandler
{
    private readonly IStudyTaskRepository _tasks;
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public UpdateTaskHandler(
        IStudyTaskRepository tasks,
        ISubjectRepository subjects,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _tasks = tasks;
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(
        Guid taskId,
        string title,
        string? notes,
        Guid? subjectId,
        DateOnly? dueDateLocal,
        int? estimatedMinutes,
        TaskPriority priority,
        DateOnly? plannedForLocalDate,
        CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        if (subjectId is { } sid && await _subjects.FindAsync(sid, cancellationToken) is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SubjectNotFound, "The linked subject does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    task.UpdateDetails(title, notes, dueDateLocal, estimatedMinutes, priority, subjectId, plannedForLocalDate, _clock.UtcNow);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>
/// Marks a task Done (BS-STU-007). Awards task XP exactly once per task identity;
/// a repeated completion returns success-with-no-change. The completion transition,
/// its ledger entry, any achievement unlocks, and cached totals commit in one
/// transaction (§8.11).
/// </summary>
public sealed class CompleteTaskHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly IStudyTaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RewardService _rewards;
    private readonly ILocalClock _clock;

    public CompleteTaskHandler(
        ILocalProfileRepository profiles,
        IStudyTaskRepository tasks,
        IUnitOfWork unitOfWork,
        RewardService rewards,
        ILocalClock clock)
    {
        _profiles = profiles;
        _tasks = tasks;
        _unitOfWork = unitOfWork;
        _rewards = rewards;
        _clock = clock;
    }

    public async Task<RewardResult> HandleAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return Failed(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        if (task.IsDeleted)
        {
            return Failed(UseCaseErrorCodes.TaskDeleted, "A deleted task cannot be completed.");
        }

        var profile = await _profiles.FindAsync(task.LocalProfileId, cancellationToken);
        if (profile is null)
        {
            return Failed(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        var now = _clock.UtcNow;

        if (task.Status is StudyTaskStatus.Done)
        {
            // Already Done: success-with-no-change (TV-09).
            return await ResultWithoutChangeAsync(task.LocalProfileId, cancellationToken);
        }

        return await CommandBoundary.ExecuteAsync(
            _unitOfWork,
            async ct =>
            {
                var firstCompletion = task.Complete(now);
                if (!firstCompletion)
                {
                    // Reopened task: the second completion persists the Done transition,
                    // but the original task-XP entry and achievement are never repeated
                    // (TV-09 / TV-18). Changed is false because no new reward was issued.
                    return await ResultWithoutChangeAsync(task.LocalProfileId, ct);
                }

                var totalXpBefore = await _rewards.TotalXpAsync(task.LocalProfileId, ct);
                var levelBefore = EconomyRules.LevelForTotalXp(totalXpBefore);

                var (entry, alreadyExisted) = await _rewards.AwardAsync(
                    task.LocalProfileId,
                    CurrencyCode.Xp,
                    EconomyRules.TaskCompletionXp,
                    LedgerSourceType.TaskCompletion,
                    task.Id.ToString(),
                    notes: "Task completion",
                    ct);

                if (entry is not null && task.CompletionRewardId is null)
                {
                    task.AttachCompletionReward(entry.Id, now);
                }

                await _rewards.EvaluateAndUnlockAchievementsAsync(task.LocalProfileId, ct);
                var totalXpAfter = await _rewards.TotalXpAsync(task.LocalProfileId, ct);
                var levelAfter = EconomyRules.LevelForTotalXp(totalXpAfter);
                profile.SetCachedRewardTotals(totalXpAfter, levelAfter, now);

                return new RewardResult(totalXpAfter, levelBefore, levelAfter, Changed: !alreadyExisted);
            },
            cancellationToken);
    }

    private async Task<RewardResult> ResultWithoutChangeAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var totalXp = await _rewards.TotalXpAsync(profileId, cancellationToken);
        var level = EconomyRules.LevelForTotalXp(totalXp);
        return new RewardResult(totalXp, level, level, Changed: false);
    }

    private static RewardResult Failed(string code, string message) =>
        new(0, 0, 0, Changed: false, new UseCaseError(code, message));
}

/// <summary>Reopens a completed task; the historical award is never removed (§8.5).</summary>
public sealed class ReopenTaskHandler
{
    private readonly IStudyTaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public ReopenTaskHandler(IStudyTaskRepository tasks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _tasks = tasks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    task.Reopen(_clock.UtcNow);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Moves a Todo task to InProgress.</summary>
public sealed class StartTaskHandler
{
    private readonly IStudyTaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public StartTaskHandler(IStudyTaskRepository tasks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _tasks = tasks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    task.Start(_clock.UtcNow);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Archives a task (BS-STU-004).</summary>
public sealed class ArchiveTaskHandler
{
    private readonly IStudyTaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public ArchiveTaskHandler(IStudyTaskRepository tasks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _tasks = tasks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid taskId, bool restore = false, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    if (restore)
                    {
                        task.Restore(_clock.UtcNow);
                    }
                    else
                    {
                        task.Archive(_clock.UtcNow);
                    }

                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Soft-deletes a task; history remains readable and awarded XP is never removed (§9.2).</summary>
public sealed class SoftDeleteTaskHandler
{
    private readonly IStudyTaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public SoftDeleteTaskHandler(IStudyTaskRepository tasks, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _tasks = tasks;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.FindAsync(taskId, cancellationToken);
        if (task is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.TaskNotFound, "The task does not exist.");
        }

        return await CommandBoundary.ExecuteAsync(
            _unitOfWork,
            () =>
            {
                task.SoftDelete(_clock.UtcNow);
                return UseCaseResult.Ok();
            },
            cancellationToken);
    }
}
