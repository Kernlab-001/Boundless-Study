namespace BoundlessStudy.Agent;

/// <summary>
/// Folds independent Windows lock and sleep signals into one paused/unpaused state.
/// Each signal source is timestamp-ordered so a delayed callback cannot undo a newer state.
/// </summary>
public sealed class SystemPauseSignalState
{
    private bool _isLocked;
    private bool _isSuspended;
    private bool _applicationProtectionFailed;
    private Guid? _applicationProtectionSessionId;
    private DateTime? _lastLockEventUtc;
    private DateTime? _lastPowerEventUtc;
    private DateTime? _lastApplicationProtectionEventUtc;

    public bool IsPaused => _isLocked || _isSuspended || _applicationProtectionFailed;

    /// <summary>Updates lock state. Returns the new aggregate state only when it changed.</summary>
    public bool? SetLocked(bool isLocked, DateTime occurredAtUtc)
    {
        var atUtc = NormalizeUtc(occurredAtUtc);
        if (_lastLockEventUtc.HasValue && atUtc <= _lastLockEventUtc.Value) return null;
        _lastLockEventUtc = atUtc;
        var wasPaused = IsPaused;
        _isLocked = isLocked;
        return wasPaused == IsPaused ? null : IsPaused;
    }

    /// <summary>Updates sleep state. Returns the new aggregate state only when it changed.</summary>
    public bool? SetSuspended(bool isSuspended, DateTime occurredAtUtc)
    {
        var atUtc = NormalizeUtc(occurredAtUtc);
        if (_lastPowerEventUtc.HasValue && atUtc <= _lastPowerEventUtc.Value) return null;
        _lastPowerEventUtc = atUtc;
        var wasPaused = IsPaused;
        _isSuspended = isSuspended;
        return wasPaused == IsPaused ? null : IsPaused;
    }

    /// <summary>
    /// Updates an application's visible-overlay enforcement state for one focus session.
    /// A stale session cannot clear or create a pause cause for the current session.
    /// </summary>
    public bool? SetApplicationProtectionFailure(Guid sessionId, bool failed, DateTime occurredAtUtc)
    {
        if (sessionId == Guid.Empty) return null;
        var atUtc = NormalizeUtc(occurredAtUtc);
        if (_applicationProtectionSessionId == sessionId
            && _lastApplicationProtectionEventUtc.HasValue
            && atUtc <= _lastApplicationProtectionEventUtc.Value)
            return null;

        var wasPaused = IsPaused;
        if (_applicationProtectionSessionId != sessionId)
        {
            _applicationProtectionSessionId = sessionId;
            _lastApplicationProtectionEventUtc = null;
            _applicationProtectionFailed = false;
        }

        if (_lastApplicationProtectionEventUtc.HasValue && atUtc <= _lastApplicationProtectionEventUtc.Value)
            return null;

        _lastApplicationProtectionEventUtc = atUtc;
        _applicationProtectionFailed = failed;
        return wasPaused == IsPaused ? null : IsPaused;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
