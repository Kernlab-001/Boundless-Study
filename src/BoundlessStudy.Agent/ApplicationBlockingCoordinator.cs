using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using BoundlessStudy.Infrastructure.Services;

namespace BoundlessStudy.Agent;

/// <summary>
/// Owns the foreground app guard and its Desktop overlay handshake. Detection alone is
/// not reported as successful enforcement: the Desktop must acknowledge a visible overlay.
/// </summary>
public sealed class ApplicationBlockingCoordinator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    private static readonly TimeSpan DesktopLease = TimeSpan.FromSeconds(2);
    private readonly SqliteDataStore _store;
    private readonly ILocalClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IWindowInterop _windowInterop;
    private ApplicationInterventionDto _snapshot = Empty();
    private long? _overlayWindowHandle;
    private uint? _overlayProcessId;
    private long _lastDesktopPollTick;
    private Guid _profileId;
    private volatile bool _runtimeReady;
    private Func<Guid, bool, DateTime?, CancellationToken, Task>? _protectionFailureHandler;

    public ApplicationBlockingCoordinator(SqliteDataStore store, ILocalClock clock, IWindowInterop? windowInterop = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _windowInterop = windowInterop ?? (OperatingSystem.IsWindows()
            ? new Win32WindowInterop()
            : throw new PlatformNotSupportedException("The application guard requires Windows."));
    }

    public ApplicationInterventionDto GetForDesktop()
    {
        Interlocked.Exchange(ref _lastDesktopPollTick, Environment.TickCount64);
        return GetSnapshot();
    }

    public ApplicationInterventionDto GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public bool IsProtectionReady(Guid sessionId)
    {
        if (!_runtimeReady) return false;
        var snapshot = GetSnapshot();
        return snapshot.State == "None"
            || (snapshot.FocusSessionId == sessionId && snapshot.State == "OverlayVisible");
    }

    public void SetProtectionFailureHandler(Func<Guid, bool, DateTime?, CancellationToken, Task> handler) =>
        _protectionFailureHandler = handler ?? throw new ArgumentNullException(nameof(handler));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        await using var repos = _store.CreateRepositories();
        var profile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
        if (profile is null) return;
        _profileId = profile.Id;
        _runtimeReady = true;
        var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            _clock,
            repos.SessionEvents,
            _windowInterop,
            new BlockingEngineOptions { PollingInterval = TimeSpan.FromMilliseconds(300) });

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    repos.Context.ChangeTracker.Clear();
                    profile = await repos.LocalProfiles.FindSingleAsync(cancellationToken);
                    if (profile is null) continue;
                    var session = await repos.FocusSessions.FindActiveByProfileAsync(profile.Id, cancellationToken);
                    var currentSnapshot = GetSnapshot();
                    if (currentSnapshot.State == "OverlayVisible")
                    {
                        if (session is { State: SessionState.Active or SessionState.Blocked }
                            && session.Id == currentSnapshot.FocusSessionId
                            && IsOverlayCurrentAndForeground(currentSnapshot, _overlayWindowHandle, _overlayProcessId)
                            && await IsTargetStillBlockedAsync(repos, session.Id, currentSnapshot.TargetKey, cancellationToken))
                        {
                            if (Environment.TickCount64 - Interlocked.Read(ref _lastDesktopPollTick) <= (long)DesktopLease.TotalMilliseconds)
                                continue;

                            await TransitionToUnblockedIfNeededAsync(repos, session.Id, cancellationToken);
                            await SetFailedAsync(session.Id, currentSnapshot.TargetKey, currentSnapshot.DisplayName,
                                "DesktopOverlayHeartbeatLost", repos, cancellationToken);
                            continue;
                        }

                        await ClearIfNeededAsync(repos, cancellationToken);
                    }
                    var appPaused = session?.State == SessionState.SystemPaused
                        && currentSnapshot.FocusSessionId == session.Id
                        && currentSnapshot.State is "Detected" or "Failed";
                    if (session is null || (session.State is not (SessionState.Active or SessionState.Blocked) && !appPaused)
                        || string.Equals(session.ProfileCode, BuiltInProfiles.FlexibleCode, StringComparison.OrdinalIgnoreCase)
                        || await repos.BlockedApps.CountEnabledAsync(profile.Id, cancellationToken) == 0)
                    {
                        await ClearIfNeededAsync(repos, cancellationToken);
                        continue;
                    }

                    var result = await engine.EvaluateForegroundAppAsync(session.Id, cancellationToken);
                    if (result.Status == InterventionStatus.OverlayRequired)
                    {
                        var key = BuildTargetKey(result.ProcessId);
                        if (key is null)
                        {
                            await SetFailedAsync(session.Id, null, result.ProcessName, "ProcessIdentityUnavailable", repos, cancellationToken);
                            continue;
                        }

                        await SetDetectedAsync(session.Id, key, result.ProcessName, result.FailureReason,
                            GetTargetBounds(result.WindowHandle));
                    }
                    else
                    {
                        await ClearIfNeededAsync(repos, cancellationToken);
                    }

                    if (IsOverlayVisible() && Environment.TickCount64 - Interlocked.Read(ref _lastDesktopPollTick) > (long)DesktopLease.TotalMilliseconds)
                    {
                        await SetFailedAsync(session.Id, _snapshot.TargetKey, _snapshot.DisplayName,
                            "DesktopOverlayHeartbeatLost", repos, cancellationToken);
                        await TransitionToUnblockedIfNeededAsync(repos, session.Id, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"BoundlessStudy.Agent: application guard tick failed ({ex.GetType().Name}).");
                    await ClearSnapshotAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal Agent shutdown.
        }
        finally
        {
            _runtimeReady = false;
            engine.StopMonitoring();
            await engine.DisposeAsync();
        }
    }

    public async Task<bool> AcknowledgeAsync(
        SqliteRepositorySet repos,
        ApplicationOverlayAckRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (request.FocusSessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.TargetKey)
                || request.TargetKey.Length > 80 || request.ErrorCode?.Length > 80
                || _snapshot.FocusSessionId != request.FocusSessionId
                || !string.Equals(_snapshot.TargetKey, request.TargetKey, StringComparison.Ordinal)
                || _snapshot.State is not ("Detected" or "Failed" or "OverlayVisible"))
                return false;

            var session = await repos.FocusSessions.FindAsync(request.FocusSessionId, cancellationToken);
            if (session is null || session.LocalProfileId != _profileId
                || (session.State is not (SessionState.Active or SessionState.Blocked)
                    && !(session.State == SessionState.SystemPaused && _snapshot.State is "Detected" or "Failed")))
                return false;

            if (request.IsVisible)
            {
                if (request.OverlayWindowHandle is not { } requestedOverlay
                    || requestedOverlay == 0
                    || !IsValidDesktopOverlay(requestedOverlay, _snapshot.TargetBounds, out var overlayProcessId)
                    || !IsSameForegroundTarget(request.TargetKey)
                    || !await IsTargetStillBlockedAsync(repos, session.Id, request.TargetKey, cancellationToken)) return false;
                if (_protectionFailureHandler is not null)
                    await _protectionFailureHandler(session.Id, false, _clock.UtcNow, cancellationToken);
                repos.Context.ChangeTracker.Clear();
                session = await repos.FocusSessions.FindAsync(request.FocusSessionId, cancellationToken);
                if (session is null || session.State == SessionState.SystemPaused)
                {
                    if (_protectionFailureHandler is not null)
                        await _protectionFailureHandler(request.FocusSessionId, true, _clock.UtcNow, cancellationToken);
                    return false;
                }
                if (session.State is not (SessionState.Active or SessionState.Blocked)) return false;
                if (_snapshot.State != "OverlayVisible")
                {
                    var recorder = new SessionEventRecorder(repos.SessionEvents, _clock);
                    var violation = new RecordViolationHandler(repos.FocusSessions, repos.UnitOfWork, _clock, recorder);
                    var payload = JsonSerializer.Serialize(new
                    {
                        target = _snapshot.DisplayName,
                        targetType = "Application",
                        intervention = "VisibleOverlay",
                        processInstance = request.TargetKey
                    });
                    var recorded = await violation.HandleAsync(session.Id, SessionEventType.Blocked,
                        _snapshot.DisplayName ?? "application", "Application", payload, cancellationToken);
                    if (!recorded.Success) return false;
                }
                SetSnapshot(new ApplicationInterventionDto("OverlayVisible", session.Id, request.TargetKey,
                    _snapshot.DisplayName, null, "OverlayVisible", ["EndSession", "GrantTemporaryException"], _clock.UtcNow,
                    _snapshot.TargetBounds));
                _overlayWindowHandle = requestedOverlay;
                _overlayProcessId = overlayProcessId;
                return true;
            }

            if (_snapshot.State == "Failed") return true;

            await RecordFailureAsync(repos, session.Id, request.TargetKey, _snapshot.DisplayName,
                request.ErrorCode ?? "OverlayNotVisible", cancellationToken);
            if (_protectionFailureHandler is not null)
                await _protectionFailureHandler(session.Id, true, _clock.UtcNow, cancellationToken);
            SetSnapshot(new ApplicationInterventionDto("Failed", session.Id, request.TargetKey,
                _snapshot.DisplayName, "The restriction overlay could not be shown.", "EnforcementFailed",
                ["EndSession", "GrantTemporaryException"], _clock.UtcNow));
            _overlayWindowHandle = null;
            _overlayProcessId = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetDetectedAsync(Guid sessionId, string targetKey, string? processName, string? reason,
                        WindowBoundsDto? targetBounds)
    {
        await _gate.WaitAsync();
        try
        {
            if (_snapshot.FocusSessionId == sessionId && _snapshot.TargetKey == targetKey
                && _snapshot.State is "OverlayVisible" or "Failed") return;
            var isNewDetection = _snapshot.FocusSessionId != sessionId || _snapshot.TargetKey != targetKey
                || _snapshot.State != "Detected";
            var oldFailedSession = _snapshot.State == "Failed" ? _snapshot.FocusSessionId : null;
            if (_snapshot.State == "OverlayVisible" && _snapshot.FocusSessionId is { } oldSession)
            {
                await using var repos = _store.CreateRepositories();
                await TransitionToUnblockedIfNeededAsync(repos, oldSession, CancellationToken.None);
            }
            if (oldFailedSession.HasValue && _protectionFailureHandler is not null)
                await _protectionFailureHandler(oldFailedSession.Value, false, _clock.UtcNow, CancellationToken.None);
            SetSnapshot(new ApplicationInterventionDto("Detected", sessionId, targetKey, processName,
                reason ?? "This application is on the active focus profile's blocked list.",
                "AwaitingOverlay", ["EndSession", "GrantTemporaryException"], _clock.UtcNow, targetBounds));
            _overlayWindowHandle = null;
            _overlayProcessId = null;
            if (isNewDetection && _protectionFailureHandler is not null)
                await _protectionFailureHandler(sessionId, true, _clock.UtcNow, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task SetFailedAsync(Guid sessionId, string? targetKey, string? processName, string reason,
        SqliteRepositorySet? repos = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync();
        try
        {
            var isNewFailure = _snapshot.State != "Failed" || _snapshot.FocusSessionId != sessionId;
            if (isNewFailure && repos is not null)
                await RecordFailureAsync(repos, sessionId, targetKey ?? "unknown", processName, reason, cancellationToken);
            SetSnapshot(new ApplicationInterventionDto("Failed", sessionId, targetKey, processName,
                "Application restriction could not be verified.", reason,
                ["EndSession", "GrantTemporaryException"], _clock.UtcNow));
            _overlayWindowHandle = null;
            _overlayProcessId = null;
            if (isNewFailure && _protectionFailureHandler is not null)
                await _protectionFailureHandler(sessionId, true, _clock.UtcNow, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task ClearIfNeededAsync(SqliteRepositorySet repos, CancellationToken cancellationToken)
    {
        Guid? sessionId = null;
        Guid? pausedSessionId = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot.State == "OverlayVisible") sessionId = _snapshot.FocusSessionId;
            if (_snapshot.State is "Detected" or "Failed") pausedSessionId = _snapshot.FocusSessionId;
            if (_snapshot.State != "None") SetSnapshot(Empty());
            _overlayWindowHandle = null;
            _overlayProcessId = null;
        }
        finally { _gate.Release(); }
        if (sessionId.HasValue)
            await TransitionToUnblockedIfNeededAsync(repos, sessionId.Value, cancellationToken);
        if (pausedSessionId.HasValue && _protectionFailureHandler is not null)
            await _protectionFailureHandler(pausedSessionId.Value, false, _clock.UtcNow, cancellationToken);
    }

    private async Task TransitionToUnblockedIfNeededAsync(SqliteRepositorySet repos, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await repos.FocusSessions.FindAsync(sessionId, cancellationToken);
        if (session?.State != SessionState.Blocked) return;
        var recorder = new SessionEventRecorder(repos.SessionEvents, _clock);
        var state = new FocusBlockStateHandler(repos.FocusSessions, repos.UnitOfWork, _clock, recorder);
        await state.HandleAsync(sessionId, false, cancellationToken);
    }

    private async Task RecordFailureAsync(SqliteRepositorySet repos, Guid sessionId, string targetKey,
        string? processName, string error, CancellationToken cancellationToken)
    {
        var recorder = new SessionEventRecorder(repos.SessionEvents, _clock);
        var handler = new RecordViolationHandler(repos.FocusSessions, repos.UnitOfWork, _clock, recorder);
        var payload = JsonSerializer.Serialize(new { target = processName, targetType = "Application", errorCode = error, processInstance = targetKey });
        await handler.HandleAsync(sessionId, SessionEventType.EnforcementFailed, processName ?? "application",
            "Application", payload, cancellationToken);
    }

    private bool IsSameForegroundTarget(string expectedKey)
    {
        var hwnd = _windowInterop.GetForegroundWindowHandle();
        if (hwnd == 0) return false;
        var pid = _windowInterop.GetWindowProcessId(hwnd);
        var (name, path) = _windowInterop.GetProcessDetails(pid);
        return BuildTargetKey(pid) == expectedKey && !BlockedApp.IsProtectedTarget(path, name);
    }

    private async Task<bool> IsTargetStillBlockedAsync(
        SqliteRepositorySet repos,
        Guid sessionId,
        string? targetKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) return false;
        var separator = targetKey.IndexOf(':');
        if (separator <= 0 || !int.TryParse(targetKey[..separator], out var pid)
            || !long.TryParse(targetKey[(separator + 1)..], out var expectedStartTicks)) return false;
        string? processName;
        string? executablePath;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return false;
            (processName, executablePath) = _windowInterop.GetProcessDetails(pid);
        }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(processName) || BlockedApp.IsProtectedTarget(executablePath, processName)) return false;

        var rules = await repos.BlockedApps.ListAsync(_profileId, cancellationToken);
        var executableName = Path.GetFileName(executablePath ?? processName);
        var normalizedName = Path.GetFileNameWithoutExtension(executableName);
        var matched = rules.Any(rule => rule.IsEnabled
            && (string.IsNullOrWhiteSpace(rule.NormalizedExecutablePath)
                ? string.Equals(rule.NormalizedExecutableName, executableName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(rule.NormalizedExecutableName), normalizedName, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(executablePath)
                    && string.Equals(rule.NormalizedExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)));
        if (!matched) return false;

        var exceptions = await repos.SessionExceptions.ListBySessionAsync(sessionId, cancellationToken);
        var now = _clock.UtcNow;
        return !exceptions.Any(exception => exception.TargetType == ExceptionTargetType.Application
            && !exception.WasAutoExpired && now >= exception.GrantedAtUtc && now < exception.ExpiresAtUtc
            && (string.Equals(exception.TargetId, processName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(exception.TargetId, executableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(exception.TargetId, executablePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(exception.TargetId), executableName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsOverlayCurrentAndForeground(ApplicationInterventionDto snapshot, long? overlayHandle, uint? overlayProcessId)
    {
        if (overlayHandle is not { } handle || handle == 0 || overlayProcessId is null || !OperatingSystem.IsWindows()) return false;
        var hwnd = (nint)handle;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetForegroundWindow() == 0) return false;
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var ownerPid);
        return ownerPid == overlayProcessId.Value
            && snapshot.TargetKey is { } targetKey
            && IsSameTargetProcessAlive(targetKey);
    }

    private static bool IsValidDesktopOverlay(long handle, WindowBoundsDto? bounds, out uint overlayProcessId)
    {
        overlayProcessId = 0;
        if (!OperatingSystem.IsWindows() || handle <= 0 || bounds is null
            || (IntPtr.Size == 4 && handle > int.MaxValue)) return false;
        var hwnd = (nint)handle;
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var rect)) return false;
        _ = GetWindowThreadProcessId(hwnd, out var ownerPid);
        try
        {
            using var process = Process.GetProcessById((int)ownerPid);
            if (!string.Equals(process.ProcessName, "BoundlessStudy.Desktop", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        var valid = rect.Left == bounds.X && rect.Top == bounds.Y
            && rect.Right - rect.Left == bounds.Width
            && rect.Bottom - rect.Top == bounds.Height;
        if (valid) overlayProcessId = ownerPid;
        return valid;
    }

    private static bool IsSameTargetProcessAlive(string targetKey)
    {
        var separator = targetKey.IndexOf(':');
        if (separator <= 0 || !int.TryParse(targetKey[..separator], out var pid)
            || !long.TryParse(targetKey[(separator + 1)..], out var expectedStartTicks)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == expectedStartTicks;
        }
        catch { return false; }
    }

    private static WindowBoundsDto? GetTargetBounds(nint hWnd)
    {
        if (hWnd == 0 || !OperatingSystem.IsWindows() || !GetWindowRect(hWnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0 ? new WindowBoundsDto(rect.Left, rect.Top, width, height) : null;
    }

    private static string? BuildTargetKey(int? processId)
    {
        if (processId is null or <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return $"{processId.Value}:{process.StartTime.ToUniversalTime().Ticks}";
        }
        catch { return null; }
    }

    private bool IsOverlayVisible() { lock (_gate) return _snapshot.State == "OverlayVisible"; }

    private async Task ClearSnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            SetSnapshot(Empty());
            _overlayWindowHandle = null;
            _overlayProcessId = null;
        }
        finally { _gate.Release(); }
    }

    private void SetSnapshot(ApplicationInterventionDto value)
    {
        lock (_gate) _snapshot = value;
    }

    private static ApplicationInterventionDto Empty() =>
        new("None", null, null, null, null, "NotApplicable", Array.Empty<string>(), DateTime.UtcNow);
}
