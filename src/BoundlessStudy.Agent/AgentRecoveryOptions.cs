namespace BoundlessStudy.Agent;

/// <summary>
/// Configuration options for the automatic recovery sweep worker.
/// </summary>
public sealed class AgentRecoveryOptions
{
    /// <summary>
    /// Periodic background sweep interval. Defaults to 1 minute.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether an immediate sweep runs during agent startup. Defaults to true.
    /// </summary>
    public bool RunStartupSweep { get; set; } = true;
}
