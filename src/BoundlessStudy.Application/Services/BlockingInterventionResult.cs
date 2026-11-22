namespace BoundlessStudy.Application.Services;

/// <summary>
/// Status of an application blocking evaluation cycle (BS-APP-004, BS-APP-007, ADR-0003).
/// </summary>
public enum InterventionStatus
{
    NoActiveSession,
    NoForegroundWindow,
    NotBlocked,
    AllowedApp,
    ExceptionActive,
    GracePeriodActive,
    ProtectedTargetBypass,
    OverlayRequired,
    Intervened,
    EnforcementFailed,
}

/// <summary>
/// The type of intervention executed on a blocked application window.
/// </summary>
public enum InterventionType
{
    None,
    GracefulMinimize,
    CloseRequest,
}

/// <summary>
/// Result of an application blocking evaluation and intervention attempt (BS-APP-004, BS-APP-007, ADR-0003).
/// </summary>
public sealed record BlockingInterventionResult
{
    public bool Intervened { get; init; }

    public InterventionStatus Status { get; init; }

    public InterventionType InterventionType { get; init; }

    public string? ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public int? ProcessId { get; init; }

    public nint WindowHandle { get; init; }

    public TimeSpan ForegroundDuration { get; init; }

    public string? MatchedRuleTarget { get; init; }

    public string? FailureReason { get; init; }

    public bool IsProtectedTarget { get; init; }

    public static BlockingInterventionResult NoActiveSession() =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.NoActiveSession,
            InterventionType = InterventionType.None,
        };

    public static BlockingInterventionResult NoForegroundWindow() =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.NoForegroundWindow,
            InterventionType = InterventionType.None,
        };

    public static BlockingInterventionResult NotBlocked(
        string? processName,
        string? executablePath = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.NotBlocked,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
        };

    public static BlockingInterventionResult AllowedApp(
        string? processName,
        string? executablePath = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.AllowedApp,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
        };

    public static BlockingInterventionResult ExceptionActive(
        string? processName,
        string? executablePath = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.ExceptionActive,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
        };

    public static BlockingInterventionResult GracePeriodActive(
        string? processName,
        string? executablePath,
        TimeSpan foregroundDuration,
        string? matchedRuleTarget = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.GracePeriodActive,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
            ForegroundDuration = foregroundDuration,
            MatchedRuleTarget = matchedRuleTarget,
        };

    public static BlockingInterventionResult ProtectedTargetBypass(
        string? processName,
        string? executablePath = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.ProtectedTargetBypass,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
            IsProtectedTarget = true,
        };

    public static BlockingInterventionResult IntervenedResult(
        InterventionType type,
        string? processName,
        string? executablePath,
        TimeSpan foregroundDuration,
        string? matchedRuleTarget = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = true,
            Status = InterventionStatus.Intervened,
            InterventionType = type,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
            ForegroundDuration = foregroundDuration,
            MatchedRuleTarget = matchedRuleTarget,
        };

    public static BlockingInterventionResult OverlayRequired(
        string? processName,
        string? executablePath,
        TimeSpan foregroundDuration,
        string? matchedRuleTarget = null,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.OverlayRequired,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
            ForegroundDuration = foregroundDuration,
            MatchedRuleTarget = matchedRuleTarget,
        };

    public static BlockingInterventionResult EnforcementFailed(
        string? processName,
        string? executablePath,
        string failureReason,
        int? processId = null,
        nint windowHandle = 0) =>
        new()
        {
            Intervened = false,
            Status = InterventionStatus.EnforcementFailed,
            InterventionType = InterventionType.None,
            ProcessName = processName,
            ExecutablePath = executablePath,
            ProcessId = processId,
            WindowHandle = windowHandle,
            FailureReason = failureReason,
        };
}
