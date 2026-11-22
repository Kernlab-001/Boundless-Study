using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Services;

namespace BoundlessStudy.Infrastructure.Services;

/// <summary>
/// Windows-specific implementation of <see cref="ISystemStateTracker"/>.
/// Tracks:
/// 1. Workstation lock/unlock via WTSRegisterSessionNotification and SessionSwitchEventArgs.
/// 2. System sleep/resume via PowerModeChangedEventArgs and WM_POWERBROADCAST.
/// 3. User idle time via GetLastInputInfo and periodic evaluation against the idle threshold.
/// </summary>
public sealed class WindowsSystemStateTracker : ISystemStateTracker
{
    // Native Win32 constants for session and power monitoring
    public const uint NOTIFY_FOR_THIS_SESSION = 0;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;

    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMESUSPEND = 0x0007;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    private readonly ILocalClock? _clock;
    private readonly object _syncRoot = new();
    private Timer? _idleTimer;
    private bool _isStarted;
    private bool _disposed;
    private bool _systemEndingRaised;
    private IntPtr _registeredWindowHandle = IntPtr.Zero;
    private TimeSpan? _idleDurationOverride;
    private TimeSpan _idleThreshold = TimeSpan.FromMinutes(5);
    private TimeSpan _idlePollInterval = TimeSpan.FromSeconds(1);

    // Reflection state for Microsoft.Win32.SystemEvents integration
    private Delegate? _sessionSwitchHandler;
    private Delegate? _powerModeHandler;
    private Delegate? _sessionEndingHandler;
    private EventInfo? _sessionSwitchEvent;
    private EventInfo? _powerModeEvent;
    private EventInfo? _sessionEndingEvent;

    public event EventHandler<WorkstationLockEventArgs>? WorkstationLockChanged;
    public event EventHandler<PowerSuspendEventArgs>? PowerSuspendChanged;
    public event EventHandler<UserIdleEventArgs>? IdleStateChanged;
    public event EventHandler<SystemEndingEventArgs>? SystemEnding;

    public bool IsWorkstationLocked { get; private set; }
    public bool IsSuspended { get; private set; }
    public bool IsIdle { get; private set; }

    /// <summary>
    /// Default idle threshold is 5 minutes (domain-business-rules.md §5).
    /// </summary>
    public TimeSpan IdleThreshold
    {
        get => _idleThreshold;
        set => _idleThreshold = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Idle threshold must be greater than zero.");
    }

    /// <summary>
    /// Polling interval for checking user idle time.
    /// </summary>
    public TimeSpan IdlePollInterval
    {
        get => _idlePollInterval;
        set => _idlePollInterval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Idle poll interval must be greater than zero.");
    }

    public WindowsSystemStateTracker(ILocalClock? clock = null)
    {
        _clock = clock;
    }

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_disposed || _isStarted)
            {
                return;
            }

            _isStarted = true;
            HookSystemEventsIfAvailable();

            _idleTimer = new Timer(
                _ => CheckIdleState(fromTimer: true),
                null,
                IdlePollInterval,
                IdlePollInterval);
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (!_isStarted)
            {
                return;
            }

            _isStarted = false;
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _idleTimer?.Dispose();
            _idleTimer = null;

            UnhookSystemEvents();

            if (_registeredWindowHandle != IntPtr.Zero)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        WTSUnRegisterSessionNotification(_registeredWindowHandle);
                    }
                }
                catch
                {
                    // Ignore unregistration failures during shutdown
                }
                _registeredWindowHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Registers a window handle for terminal services session notifications (WTS).
    /// </summary>
    public bool RegisterSessionNotification(IntPtr hWnd)
    {
        lock (_syncRoot)
        {
            if (!OperatingSystem.IsWindows() || hWnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (WTSRegisterSessionNotification(hWnd, NOTIFY_FOR_THIS_SESSION))
                {
                    _registeredWindowHandle = hWnd;
                    return true;
                }
            }
            catch
            {
                // Fall back gracefully if WTS API is unavailable
            }

            return false;
        }
    }

    /// <summary>
    /// Calculates elapsed time since last mouse or keyboard input using GetLastInputInfo.
    /// </summary>
    public TimeSpan GetIdleDuration()
    {
        if (_idleDurationOverride.HasValue)
        {
            return _idleDurationOverride.Value;
        }

        if (!OperatingSystem.IsWindows())
        {
            return TimeSpan.Zero;
        }

        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                uint currentTick = unchecked((uint)Environment.TickCount);
                uint idleMillis = currentTick - lii.dwTime;
                return TimeSpan.FromMilliseconds(idleMillis);
            }
        }
        catch
        {
            // Fall back to zero if GetLastInputInfo fails
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Sets an idle duration override for testing purposes.
    /// </summary>
    public void SetIdleDurationOverride(TimeSpan? idleDuration)
    {
        _idleDurationOverride = idleDuration;
        CheckIdleState();
    }

    /// <summary>
    /// Processes a WTSRegisterSessionNotification message or WTS session change reason.
    /// </summary>
    public void HandleSessionSwitch(int reason, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        if (reason == WTS_SESSION_LOCK)
        {
            SetLockState(true, now);
        }
        else if (reason == WTS_SESSION_UNLOCK)
        {
            SetLockState(false, now);
        }
    }

    /// <summary>
    /// Updates workstation lock state directly.
    /// </summary>
    public void HandleSessionLockChanged(bool isLocked, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        SetLockState(isLocked, now);
    }

    /// <summary>
    /// Processes a WM_POWERBROADCAST message code.
    /// </summary>
    public void HandlePowerBroadcast(int eventCode, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        if (eventCode == PBT_APMSUSPEND)
        {
            SetSuspendState(true, now);
        }
        else if (eventCode is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
        {
            SetSuspendState(false, now);
        }
    }

    /// <summary>
    /// Updates system suspend/sleep state directly.
    /// </summary>
    public void HandlePowerSuspendChanged(bool isSuspended, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        SetSuspendState(isSuspended, now);
    }

    /// <summary>
    /// Processes a Windows SessionEnding notification. Duplicate logoff/shutdown
    /// notifications are coalesced because Windows may deliver the signal through
    /// more than one event path during shutdown.
    /// </summary>
    public void HandleSessionEnding(SystemEndingKind reason, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        lock (_syncRoot)
        {
            if (_systemEndingRaised || _disposed)
            {
                return;
            }

            _systemEndingRaised = true;
        }

        SystemEnding?.Invoke(this, new SystemEndingEventArgs(reason, now));
    }

    /// <summary>
    /// Updates user idle state directly.
    /// </summary>
    public void HandleIdleStateChanged(bool isIdle, TimeSpan? idleDuration = null, DateTime? atUtc = null)
    {
        var now = NormalizeUtc(atUtc ?? _clock?.UtcNow ?? DateTime.UtcNow);
        var duration = idleDuration ?? (isIdle ? IdleThreshold : TimeSpan.Zero);

        lock (_syncRoot)
        {
            if (IsIdle == isIdle)
            {
                return;
            }

            IsIdle = isIdle;
        }

        IdleStateChanged?.Invoke(this, new UserIdleEventArgs(isIdle, duration, now));
    }

    private void SetLockState(bool isLocked, DateTime atUtc)
    {
        lock (_syncRoot)
        {
            if (IsWorkstationLocked == isLocked)
            {
                return;
            }

            IsWorkstationLocked = isLocked;
        }

        WorkstationLockChanged?.Invoke(this, new WorkstationLockEventArgs(isLocked, atUtc));
    }

    private void SetSuspendState(bool isSuspended, DateTime atUtc)
    {
        lock (_syncRoot)
        {
            if (IsSuspended == isSuspended)
            {
                return;
            }

            IsSuspended = isSuspended;
        }

        PowerSuspendChanged?.Invoke(this, new PowerSuspendEventArgs(isSuspended, atUtc));
    }

    private void CheckIdleState(bool fromTimer = false)
    {
        var duration = GetIdleDuration();
        bool isIdle = duration >= IdleThreshold;
        var now = NormalizeUtc(_clock?.UtcNow ?? DateTime.UtcNow);

        bool stateChanged = false;
        lock (_syncRoot)
        {
            // A Timer callback may already be queued when Stop disposes the timer.
            // Do not let that stale callback publish new state after monitoring ended.
            if (_disposed || (fromTimer && !_isStarted))
            {
                return;
            }

            if (IsIdle != isIdle)
            {
                IsIdle = isIdle;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            IdleStateChanged?.Invoke(this, new UserIdleEventArgs(isIdle, duration, now));
        }
    }

    /// <summary>
    /// Dynamically binds to Microsoft.Win32.SystemEvents if available in the runtime environment.
    /// </summary>
    private void HookSystemEventsIfAvailable()
    {
        try
        {
            var systemEventsType = Type.GetType("Microsoft.Win32.SystemEvents, Microsoft.Win32.SystemEvents")
                ?? Type.GetType("Microsoft.Win32.SystemEvents, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Microsoft.Win32.SystemEvents"))
                    .FirstOrDefault(t => t is not null);

            if (systemEventsType is null)
            {
                return;
            }

            // Hook SessionSwitch event
            _sessionSwitchEvent = systemEventsType.GetEvent("SessionSwitch", BindingFlags.Public | BindingFlags.Static);
            if (_sessionSwitchEvent is not null)
            {
                var handlerMethod = GetType().GetMethod(nameof(OnSystemEventsSessionSwitch), BindingFlags.NonPublic | BindingFlags.Instance);
                if (handlerMethod is not null)
                {
                    _sessionSwitchHandler = Delegate.CreateDelegate(_sessionSwitchEvent.EventHandlerType!, this, handlerMethod);
                    _sessionSwitchEvent.AddEventHandler(null, _sessionSwitchHandler);
                }
            }

            // Hook PowerModeChanged event
            _powerModeEvent = systemEventsType.GetEvent("PowerModeChanged", BindingFlags.Public | BindingFlags.Static);
            if (_powerModeEvent is not null)
            {
                var handlerMethod = GetType().GetMethod(nameof(OnSystemEventsPowerModeChanged), BindingFlags.NonPublic | BindingFlags.Instance);
                if (handlerMethod is not null)
                {
                    _powerModeHandler = Delegate.CreateDelegate(_powerModeEvent.EventHandlerType!, this, handlerMethod);
                    _powerModeEvent.AddEventHandler(null, _powerModeHandler);
                }
            }

            // SessionEnding is distinct from SessionSwitch.Logoff: it provides a
            // single pre-exit signal for both user logoff and machine shutdown.
            _sessionEndingEvent = systemEventsType.GetEvent("SessionEnding", BindingFlags.Public | BindingFlags.Static);
            if (_sessionEndingEvent is not null)
            {
                var handlerMethod = GetType().GetMethod(nameof(OnSystemEventsSessionEnding), BindingFlags.NonPublic | BindingFlags.Instance);
                if (handlerMethod is not null)
                {
                    _sessionEndingHandler = Delegate.CreateDelegate(_sessionEndingEvent.EventHandlerType!, this, handlerMethod);
                    _sessionEndingEvent.AddEventHandler(null, _sessionEndingHandler);
                }
            }
        }
        catch
        {
            // SystemEvents unavailable in current execution context; ignore
        }
    }

    private void UnhookSystemEvents()
    {
        try
        {
            if (_sessionSwitchEvent is not null && _sessionSwitchHandler is not null)
            {
                _sessionSwitchEvent.RemoveEventHandler(null, _sessionSwitchHandler);
                _sessionSwitchHandler = null;
                _sessionSwitchEvent = null;
            }

            if (_powerModeEvent is not null && _powerModeHandler is not null)
            {
                _powerModeEvent.RemoveEventHandler(null, _powerModeHandler);
                _powerModeHandler = null;
                _powerModeEvent = null;
            }

            if (_sessionEndingEvent is not null && _sessionEndingHandler is not null)
            {
                _sessionEndingEvent.RemoveEventHandler(null, _sessionEndingHandler);
                _sessionEndingHandler = null;
                _sessionEndingEvent = null;
            }
        }
        catch
        {
            // Ignore during cleanup
        }
    }

    private void OnSystemEventsSessionSwitch(object? sender, object? e)
    {
        if (e is null) return;
        try
        {
            var reasonProp = e.GetType().GetProperty("Reason");
            if (reasonProp is null) return;

            var reasonVal = reasonProp.GetValue(e)?.ToString();
            var now = NormalizeUtc(_clock?.UtcNow ?? DateTime.UtcNow);

            if (string.Equals(reasonVal, "SessionLock", StringComparison.OrdinalIgnoreCase))
            {
                SetLockState(true, now);
            }
            else if (string.Equals(reasonVal, "SessionUnlock", StringComparison.OrdinalIgnoreCase))
            {
                SetLockState(false, now);
            }
        }
        catch
        {
            // Defensive against unexpected event argument structures
        }
    }

    private void OnSystemEventsPowerModeChanged(object? sender, object? e)
    {
        if (e is null) return;
        try
        {
            var modeProp = e.GetType().GetProperty("Mode");
            if (modeProp is null) return;

            var modeVal = modeProp.GetValue(e)?.ToString();
            var now = NormalizeUtc(_clock?.UtcNow ?? DateTime.UtcNow);

            if (string.Equals(modeVal, "Suspend", StringComparison.OrdinalIgnoreCase))
            {
                SetSuspendState(true, now);
            }
            else if (string.Equals(modeVal, "Resume", StringComparison.OrdinalIgnoreCase))
            {
                SetSuspendState(false, now);
            }
        }
        catch
        {
            // Defensive against unexpected event argument structures
        }
    }

    private void OnSystemEventsSessionEnding(object? sender, object? e)
    {
        if (e is null) return;
        try
        {
            var reasonProp = e.GetType().GetProperty("Reason");
            var reasonVal = reasonProp?.GetValue(e)?.ToString();
            var reason = string.Equals(reasonVal, "Logoff", StringComparison.OrdinalIgnoreCase)
                ? SystemEndingKind.Logoff
                : string.Equals(reasonVal, "Shutdown", StringComparison.OrdinalIgnoreCase)
                    ? SystemEndingKind.Shutdown
                    : (SystemEndingKind?)null;
            if (reason.HasValue)
            {
                HandleSessionEnding(reason.Value, _clock?.UtcNow ?? DateTime.UtcNow);
            }
        }
        catch
        {
            // A system event is advisory; malformed args must not destabilize shutdown.
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
