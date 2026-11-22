namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// A study task (conceptual-data-model.md §4.3, domain-business-rules.md §9.2).
/// Named <c>StudyTask</c> to avoid the <c>System.Threading.Tasks.Task</c> conflict.
/// Deletion is soft; completion awards at most one task-XP entry (ECONOMY-1 §8.5).
/// </summary>
public class StudyTask
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 4000;

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public Guid? SubjectId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Notes { get; private set; } = string.Empty;

    public DateOnly? DueDateLocal { get; private set; }

    public int? EstimatedMinutes { get; private set; }

    public TaskPriority Priority { get; private set; } = TaskPriority.Normal;

    public StudyTaskStatus Status { get; private set; } = StudyTaskStatus.Todo;

    public DateOnly? PlannedForLocalDate { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public Guid? CompletionRewardId { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private StudyTask()
    {
    }

    public static StudyTask Create(
        Guid localProfileId,
        string title,
        DateTime createdAtUtc,
        Guid? subjectId = null,
        string? notes = null,
        DateOnly? dueDateLocal = null,
        int? estimatedMinutes = null,
        TaskPriority priority = TaskPriority.Normal,
        DateOnly? plannedForLocalDate = null)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("TASK_TITLE_REQUIRED", "A task title is required.");
        }

        if (trimmed.Length > MaxTitleLength)
        {
            throw new DomainException("TASK_TITLE_TOO_LONG", $"A task title may be at most {MaxTitleLength} characters.");
        }

        var noteText = (notes ?? string.Empty).Trim();
        if (noteText.Length > MaxNotesLength)
        {
            throw new DomainException("TASK_NOTES_TOO_LONG", $"Task notes may be at most {MaxNotesLength} characters.");
        }

        if (estimatedMinutes is not null)
        {
            ValueObjects.EstimateMinutes.Create(estimatedMinutes.Value);
        }

        if (priority is not (TaskPriority.Low or TaskPriority.Normal or TaskPriority.High))
        {
            throw new DomainException("TASK_PRIORITY_INVALID", "Priority must be Low, Normal, or High.");
        }

        return new StudyTask
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            SubjectId = subjectId,
            Title = trimmed,
            Notes = noteText,
            DueDateLocal = dueDateLocal,
            EstimatedMinutes = estimatedMinutes,
            Priority = priority,
            Status = StudyTaskStatus.Todo,
            PlannedForLocalDate = plannedForLocalDate,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void UpdateDetails(
        string title,
        string? notes,
        DateOnly? dueDateLocal,
        int? estimatedMinutes,
        TaskPriority priority,
        Guid? subjectId,
        DateOnly? plannedForLocalDate,
        DateTime atUtc)
    {
        EnsureNotDeleted("be edited");

        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("TASK_TITLE_REQUIRED", "A task title is required.");
        }

        if (trimmed.Length > MaxTitleLength)
        {
            throw new DomainException("TASK_TITLE_TOO_LONG", $"A task title may be at most {MaxTitleLength} characters.");
        }

        var noteText = (notes ?? string.Empty).Trim();
        if (noteText.Length > MaxNotesLength)
        {
            throw new DomainException("TASK_NOTES_TOO_LONG", $"Task notes may be at most {MaxNotesLength} characters.");
        }

        if (estimatedMinutes is not null)
        {
            ValueObjects.EstimateMinutes.Create(estimatedMinutes.Value);
        }

        if (priority is not (TaskPriority.Low or TaskPriority.Normal or TaskPriority.High))
        {
            throw new DomainException("TASK_PRIORITY_INVALID", "Priority must be Low, Normal, or High.");
        }

        Title = trimmed;
        Notes = noteText;
        DueDateLocal = dueDateLocal;
        EstimatedMinutes = estimatedMinutes;
        Priority = priority;
        SubjectId = subjectId;
        PlannedForLocalDate = plannedForLocalDate;
        Touch(atUtc);
    }

    public void SetPlannedForLocalDate(DateOnly? localDate, DateTime atUtc)
    {
        EnsureNotDeleted("be planned");
        PlannedForLocalDate = localDate;
        Touch(atUtc);
    }

    /// <summary>
    /// Moves a Todo or InProgress task to Done. Returns true only for the task's
    /// first completion, which is the one completion that earns task XP; a task
    /// that was reopened and completed again still reaches Done but returns false.
    /// Completing an already-Done task is a no-op returning false.
    /// </summary>
    public bool Complete(DateTime completedAtUtc)
    {
        EnsureNotDeleted("be completed");

        if (Status is StudyTaskStatus.Done)
        {
            return false;
        }

        if (Status is not (StudyTaskStatus.Todo or StudyTaskStatus.InProgress))
        {
            throw new DomainException("TASK_TRANSITION_INVALID", $"A task in status {Status} cannot be completed.");
        }

        // CompletedAtUtc is the permanent "has ever completed" marker. It is kept
        // across Reopen so a second completion can change status without earning a
        // second award (ECONOMY-1 §8.5, TV-18).
        var firstCompletion = CompletedAtUtc is null;
        Status = StudyTaskStatus.Done;
        CompletedAtUtc ??= completedAtUtc;
        Touch(completedAtUtc);
        return firstCompletion;
    }

    /// <summary>Reopening a completed task never removes its historical award.</summary>
    public void Reopen(DateTime atUtc)
    {
        if (Status is not StudyTaskStatus.Done)
        {
            throw new DomainException("TASK_TRANSITION_INVALID", "Only a completed task can be reopened.");
        }

        Status = StudyTaskStatus.Todo;
        // CompletedAtUtc and CompletionRewardId are intentionally retained:
        // re-completion must not award XP again (ECONOMY-1 §8.5).
        Touch(atUtc);
    }

    public void Start(DateTime atUtc)
    {
        EnsureNotDeleted("be started");
        if (Status is not StudyTaskStatus.Todo)
        {
            throw new DomainException("TASK_TRANSITION_INVALID", $"A task in status {Status} cannot move to InProgress.");
        }

        Status = StudyTaskStatus.InProgress;
        Touch(atUtc);
    }

    public void BackToTodo(DateTime atUtc)
    {
        if (Status is not StudyTaskStatus.InProgress)
        {
            throw new DomainException("TASK_TRANSITION_INVALID", $"A task in status {Status} cannot move back to Todo.");
        }

        Status = StudyTaskStatus.Todo;
        Touch(atUtc);
    }

    public void Archive(DateTime atUtc)
    {
        if (Status is not (StudyTaskStatus.Todo or StudyTaskStatus.InProgress or StudyTaskStatus.Done))
        {
            throw new DomainException("TASK_TRANSITION_INVALID", $"A task in status {Status} cannot be archived.");
        }

        Status = StudyTaskStatus.Archived;
        Touch(atUtc);
    }

    public void Restore(DateTime atUtc)
    {
        if (Status is not StudyTaskStatus.Archived)
        {
            throw new DomainException("TASK_TRANSITION_INVALID", "Only an archived task can be restored.");
        }

        Status = StudyTaskStatus.Todo;
        Touch(atUtc);
    }

    /// <summary>Soft delete. Retains history; previously awarded XP is never removed.</summary>
    public void SoftDelete(DateTime deletedAtUtc)
    {
        IsDeleted = true;
        DeletedAtUtc = deletedAtUtc;
        Touch(deletedAtUtc);
    }

    /// <summary>Links the one-time task-completion ledger entry for audit.</summary>
    public void AttachCompletionReward(Guid ledgerEntryId, DateTime atUtc)
    {
        if (CompletionRewardId is not null && CompletionRewardId != ledgerEntryId)
        {
            throw new DomainException("TASK_REWARD_ALREADY_SET", "A completion reward is already linked to this task.");
        }

        CompletionRewardId = ledgerEntryId;
        Touch(atUtc);
    }

    /// <summary>True when the task has ever been completed; reopening keeps this true.</summary>
    public bool HasCompletionAward => CompletedAtUtc is not null;

    private void EnsureNotDeleted(string operation)
    {
        if (IsDeleted)
        {
            throw new DomainException("TASK_DELETED", $"A deleted task cannot {operation}.");
        }
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
