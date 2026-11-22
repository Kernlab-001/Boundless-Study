namespace BoundlessStudy.Application.Services;

/// <summary>
/// An immutable descriptor representing an operating system process discovered during enumeration.
///
/// Privacy and safety constraints:
/// - NEVER captures window titles, URLs, command-line arguments, or user personal data.
/// - Identifies whether the process is protected against blocking (system critical, accessibility tool, or product component).
/// - Identifies whether the process was accessible for full inspection (e.g. executable path).
/// </summary>
public sealed record ProcessDescriptor(
    int ProcessId,
    string ProcessName,
    string ExecutableName,
    string? ExecutablePath,
    bool IsProtected,
    bool IsAccessible)
{
    /// <summary>
    /// Convenient alias for <see cref="ProcessId"/>.
    /// </summary>
    public int Id => ProcessId;

    /// <summary>
    /// Parameterless constructor for deserialization and object initializer syntax.
    /// </summary>
    public ProcessDescriptor() : this(0, string.Empty, string.Empty, null, false, false)
    {
    }
}
