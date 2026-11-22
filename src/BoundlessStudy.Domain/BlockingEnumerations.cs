namespace BoundlessStudy.Domain;

/// <summary>Blocking, exception, and backup enumerations (conceptual-data-model.md §4.8–4.11, §4.16).</summary>
public enum ExceptionTargetType
{
    Application,
    Website,
}

public enum ExceptionReasonCategory
{
    Technical,
    Accessibility,
    StudyResource,
    Emergency,
    Other,
}

public enum BackupReason
{
    Manual,
    Automatic,
    PreMigration,
    PreRestore,
}
