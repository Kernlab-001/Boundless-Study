namespace BoundlessStudy.Domain.ValueObjects;

/// <summary>
/// Task estimate: 5–600 minutes in 5-minute increments (conceptual-data-model.md §4.3,
/// domain-business-rules.md §9.2). Null is represented by the entity (no estimate).
/// </summary>
public readonly record struct EstimateMinutes
{
    public const int Min = 5;
    public const int Max = 600;
    public const int Step = 5;

    public int Minutes { get; }

    private EstimateMinutes(int minutes)
    {
        Minutes = minutes;
    }

    public static EstimateMinutes Create(int minutes)
    {
        if (minutes < Min || minutes > Max)
        {
            throw new DomainException("ESTIMATE_OUT_OF_RANGE", $"Estimated minutes must be between {Min} and {Max}.");
        }

        if (minutes % Step != 0)
        {
            throw new DomainException("ESTIMATE_NOT_IN_STEP", $"Estimated minutes must be in {Step}-minute increments.");
        }

        return new EstimateMinutes(minutes);
    }
}

/// <summary>
/// Planned focus duration: 300–10,800 seconds (5–180 minutes) in 5-minute increments
/// (conceptual-data-model.md §4.5, domain-business-rules.md §5).
/// </summary>
public readonly record struct PlannedFocusSeconds
{
    public const int Min = 300;
    public const int Max = 10_800;
    public const int Step = 300;

    public int Seconds { get; }

    private PlannedFocusSeconds(int seconds)
    {
        Seconds = seconds;
    }

    public static PlannedFocusSeconds FromMinutes(int minutes) => Create(minutes * 60);

    public static PlannedFocusSeconds Create(int seconds)
    {
        if (seconds < Min || seconds > Max)
        {
            throw new DomainException("PLANNED_DURATION_OUT_OF_RANGE", "Planned focus duration must be between 5 and 180 minutes.");
        }

        if (seconds % Step != 0)
        {
            throw new DomainException("PLANNED_DURATION_NOT_IN_STEP", "Planned focus duration must be in 5-minute increments.");
        }

        return new PlannedFocusSeconds(seconds);
    }
}

/// <summary>
/// Planned break duration: 60–3,600 seconds (1–60 minutes) (conceptual-data-model.md §4.6,
/// domain-business-rules.md §6). No step requirement is defined, so any value in range is valid.
/// </summary>
public readonly record struct PlannedBreakSeconds
{
    public const int Min = 60;
    public const int Max = 3_600;

    public int Seconds { get; }

    private PlannedBreakSeconds(int seconds)
    {
        Seconds = seconds;
    }

    public static PlannedBreakSeconds Create(int seconds)
    {
        if (seconds < Min || seconds > Max)
        {
            throw new DomainException("BREAK_DURATION_OUT_OF_RANGE", "Planned break duration must be between 1 and 60 minutes.");
        }

        return new PlannedBreakSeconds(seconds);
    }
}

/// <summary>A SHA-256 checksum as exactly 64 lowercase hexadecimal characters (conceptual-data-model.md §4.16).</summary>
public readonly record struct Sha256Hex
{
    public const int Length = 64;

    public string Value { get; }

    private Sha256Hex(string value)
    {
        Value = value;
    }

    public static Sha256Hex Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != Length
            || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new DomainException("SHA256_INVALID", "A SHA-256 checksum must be 64 hexadecimal characters.");
        }

        return new Sha256Hex(value.ToLowerInvariant());
    }
}
