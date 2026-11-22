namespace BoundlessStudy.Application.Services;

/// <summary>
/// Workstation lock state enum for host system state monitoring.
/// </summary>
public enum WorkstationLockState
{
    Unlocked,
    Locked,
}

/// <summary>
/// Power suspend/sleep state enum for host system state monitoring.
/// </summary>
public enum PowerSuspendState
{
    Active,
    Suspended,
}

/// <summary>
/// User idle state enum for host system state monitoring.
/// </summary>
public enum UserIdleState
{
    Active,
    Idle,
}

/// <summary>
/// Event arguments for workstation lock/unlock state transitions.
/// </summary>
public sealed class WorkstationLockEventArgs : EventArgs
{
    public bool IsLocked { get; }
    public DateTime OccurredAtUtc { get; }

    public WorkstationLockEventArgs(bool isLocked, DateTime occurredAtUtc)
    {
        IsLocked = isLocked;
        OccurredAtUtc = occurredAtUtc;
    }
}

/// <summary>
/// Event arguments for system sleep/resume (power suspend) transitions.
/// </summary>
public sealed class PowerSuspendEventArgs : EventArgs
{
    public bool IsSuspended { get; }
    public DateTime OccurredAtUtc { get; }

    public PowerSuspendEventArgs(bool isSuspended, DateTime occurredAtUtc)
    {
        IsSuspended = isSuspended;
        OccurredAtUtc = occurredAtUtc;
    }
}

/// <summary>
/// Event arguments for user idle state changes (threshold reached or input resumed).
/// </summary>
public sealed class UserIdleEventArgs : EventArgs
{
    public bool IsIdle { get; }
    public TimeSpan IdleDuration { get; }
    public DateTime OccurredAtUtc { get; }

    public UserIdleEventArgs(bool isIdle, TimeSpan idleDuration, DateTime occurredAtUtc)
    {
        IsIdle = isIdle;
        IdleDuration = idleDuration;
        OccurredAtUtc = occurredAtUtc;
    }
}

/// <summary>Reason the current Windows session is ending.</summary>
public enum SystemEndingKind
{
    Logoff,
    Shutdown,
}

/// <summary>One-shot notification that Windows is ending this user session.</summary>
public sealed class SystemEndingEventArgs : EventArgs
{
    public SystemEndingKind Reason { get; }
    public DateTime OccurredAtUtc { get; }

    public SystemEndingEventArgs(SystemEndingKind reason, DateTime occurredAtUtc)
    {
        Reason = reason;
        OccurredAtUtc = occurredAtUtc;
    }
}

/// <summary>
/// State tracker port for host workstation lock, system power (sleep/resume), and user idle time.
/// Infrastructure provides the Windows implementation.
/// </summary>
public interface ISystemStateTracker : IDisposable
{
    event EventHandler<WorkstationLockEventArgs>? WorkstationLockChanged;
    event EventHandler<PowerSuspendEventArgs>? PowerSuspendChanged;
    event EventHandler<UserIdleEventArgs>? IdleStateChanged;
    event EventHandler<SystemEndingEventArgs>? SystemEnding;

    bool IsWorkstationLocked { get; }
    bool IsSuspended { get; }
    bool IsIdle { get; }

    /// <summary>
    /// Inactivity duration threshold before user is considered idle (default 5 minutes per domain rules).
    /// </summary>
    TimeSpan IdleThreshold { get; set; }

    /// <summary>
    /// Gets the duration of continuous user inactivity since last keyboard/mouse input.
    /// </summary>
    TimeSpan GetIdleDuration();

    void Start();
    void Stop();
}
