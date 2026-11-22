namespace BoundlessStudy.Domain;

/// <summary>Task and profile enumerations (conceptual-data-model.md §4.2–4.3).</summary>
public enum TaskPriority
{
    Low,
    Normal,
    High,
}

public enum StudyTaskStatus
{
    Todo,
    InProgress,
    Done,
    Archived,
}

/// <summary>Installation mode is display/capability state, not identity (conceptual-data-model.md §4.1).</summary>
public enum InstallationMode
{
    Full,
    Limited,
}
