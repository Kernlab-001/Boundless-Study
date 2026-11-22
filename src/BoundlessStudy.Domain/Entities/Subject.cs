namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// A study subject (conceptual-data-model.md §4.2, domain-business-rules.md §9.1).
/// Archiving hides the subject from new-task selection but keeps history readable.
/// </summary>
public class Subject
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    public Guid Id { get; private set; }

    public Guid LocalProfileId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public bool IsArchived { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    private Subject()
    {
    }

    public static Subject Create(Guid localProfileId, string name, string? description, DateTime createdAtUtc)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("SUBJECT_NAME_REQUIRED", "A subject name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("SUBJECT_NAME_TOO_LONG", $"A subject name may be at most {MaxNameLength} characters.");
        }

        var desc = (description ?? string.Empty).Trim();
        if (desc.Length > MaxDescriptionLength)
        {
            throw new DomainException("SUBJECT_DESCRIPTION_TOO_LONG", $"A subject description may be at most {MaxDescriptionLength} characters.");
        }

        return new Subject
        {
            Id = Guid.NewGuid(),
            LocalProfileId = localProfileId,
            Name = trimmed,
            Description = desc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void Rename(string name, DateTime atUtc)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainException("SUBJECT_NAME_REQUIRED", "A subject name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException("SUBJECT_NAME_TOO_LONG", $"A subject name may be at most {MaxNameLength} characters.");
        }

        Name = trimmed;
        Touch(atUtc);
        // Note: existing focus-session subject-name snapshots are never rewritten
        // (conceptual-data-model.md §4.5 invariant).
    }

    public void SetDescription(string? description, DateTime atUtc)
    {
        var desc = (description ?? string.Empty).Trim();
        if (desc.Length > MaxDescriptionLength)
        {
            throw new DomainException("SUBJECT_DESCRIPTION_TOO_LONG", $"A subject description may be at most {MaxDescriptionLength} characters.");
        }

        Description = desc;
        Touch(atUtc);
    }

    public void Archive(DateTime atUtc)
    {
        IsArchived = true;
        Touch(atUtc);
    }

    public void Restore(DateTime atUtc)
    {
        IsArchived = false;
        Touch(atUtc);
    }

    private void Touch(DateTime atUtc) => UpdatedAtUtc = atUtc;
}
