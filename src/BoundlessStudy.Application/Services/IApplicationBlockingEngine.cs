namespace BoundlessStudy.Application.Services;

/// <summary>
/// Application blocking engine port (BS-APP-004, BS-APP-007, ADR-0003).
/// Detects active foreground applications, enforces the 3-second grace period,
/// respects allowed apps and active session exceptions, revalidates protected targets,
/// performs graceful intervention, and halts enforcement outside active focus sessions.
/// </summary>
public interface IApplicationBlockingEngine
{
    /// <summary>
    /// Indicates whether active foreground monitoring is running.
    /// </summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// The identifier of the active focus session currently monitored, if any.
    /// </summary>
    Guid? ActiveSessionId { get; }

    /// <summary>
    /// Event raised whenever an intervention (minimize or close request) is successfully performed.
    /// </summary>
    event EventHandler<BlockingInterventionResult>? InterventionOccurred;

    /// <summary>
    /// Starts monitoring for the specified active focus session.
    /// </summary>
    void StartMonitoring(Guid focusSessionId);

    /// <summary>
    /// Stops monitoring and resets all tracking state.
    /// </summary>
    void StopMonitoring();

    /// <summary>
    /// Resets the foreground grace period tracking.
    /// </summary>
    void ResetGracePeriod();

    /// <summary>
    /// Evaluates the current foreground window against the monitored session's rules.
    /// </summary>
    Task<BlockingInterventionResult> EvaluateForegroundAppAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the current foreground window against the specified focus session's rules.
    /// </summary>
    Task<BlockingInterventionResult> EvaluateForegroundAppAsync(Guid focusSessionId, CancellationToken cancellationToken = default);
}
