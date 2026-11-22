using System.Text.Json;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Infrastructure.Services;

/// <summary>
/// Configuration options for <see cref="WindowsApplicationBlockingEngine"/>.
/// </summary>
public sealed class BlockingEngineOptions
{
    /// <summary>Continuous duration in foreground before intervention occurs (BS-APP-004: 3 seconds).</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Polling interval for background monitoring.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Retained for source compatibility. Process-mutating close/minimize actions are
    /// prohibited by ADR-0003 and this option is intentionally ignored.
    /// </summary>
    public bool EnableStrictCloseRequest { get; init; }

    /// <summary>Set of application names or executable paths explicitly allowed.</summary>
    public IReadOnlyCollection<string>? AllowedApplications { get; init; }
}

/// <summary>
/// Windows application blocking engine (BS-APP-004, BS-APP-007, ADR-0003).
/// Detects active foreground application, enforces 3-second grace period,
/// respects allowed apps and active session exceptions, revalidates protected targets,
/// performs graceful intervention, and halts enforcement outside active focus sessions.
/// </summary>
public sealed class WindowsApplicationBlockingEngine : IApplicationBlockingEngine, IDisposable, IAsyncDisposable
{
    private readonly IFocusSessionRepository _sessionRepo;
    private readonly IBlockedAppRepository _blockedAppRepo;
    private readonly ISessionExceptionRepository _exceptionRepo;
    private readonly IFocusProfileRepository _profileRepo;
    private readonly ILocalClock _clock;
    private readonly ISessionEventRepository? _eventRepo;
    private readonly IWindowInterop _windowInterop;
    private readonly BlockingEngineOptions _options;
    private readonly HashSet<string> _allowedApps;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);

    private Guid? _activeSessionId;
    private bool _isMonitoring;
    private string? _currentBlockedTargetKey;
    private DateTime? _foregroundEnteredUtc;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private bool _disposed;

    public event EventHandler<BlockingInterventionResult>? InterventionOccurred;

    public bool IsMonitoring
    {
        get
        {
            lock (_stateLock)
            {
                return _isMonitoring;
            }
        }
    }

    public Guid? ActiveSessionId
    {
        get
        {
            lock (_stateLock)
            {
                return _activeSessionId;
            }
        }
    }

    public WindowsApplicationBlockingEngine(
        IFocusSessionRepository sessionRepo,
        IBlockedAppRepository blockedAppRepo,
        ISessionExceptionRepository exceptionRepo,
        IFocusProfileRepository profileRepo,
        ILocalClock clock,
        ISessionEventRepository? eventRepo = null,
        IWindowInterop? windowInterop = null,
        BlockingEngineOptions? options = null)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _blockedAppRepo = blockedAppRepo ?? throw new ArgumentNullException(nameof(blockedAppRepo));
        _exceptionRepo = exceptionRepo ?? throw new ArgumentNullException(nameof(exceptionRepo));
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _eventRepo = eventRepo;
        _options = options ?? new BlockingEngineOptions();
        _windowInterop = windowInterop ?? (OperatingSystem.IsWindows() ? new Win32WindowInterop() : throw new PlatformNotSupportedException("Win32WindowInterop requires Windows."));

        _allowedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_options.AllowedApplications is not null)
        {
            foreach (var app in _options.AllowedApplications)
            {
                if (!string.IsNullOrWhiteSpace(app))
                {
                    _allowedApps.Add(app.Trim());
                }
            }
        }
    }

    /// <summary>
    /// Adds an application to the allowed list (bypasses blocking).
    /// </summary>
    public void AddAllowedApplication(string applicationNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(applicationNameOrPath))
        {
            return;
        }

        lock (_stateLock)
        {
            _allowedApps.Add(applicationNameOrPath.Trim());
        }
    }

    /// <summary>
    /// Removes an application from the allowed list.
    /// </summary>
    public void RemoveAllowedApplication(string applicationNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(applicationNameOrPath))
        {
            return;
        }

        lock (_stateLock)
        {
            _allowedApps.Remove(applicationNameOrPath.Trim());
        }
    }

    public void StartMonitoring(Guid focusSessionId)
    {
        lock (_stateLock)
        {
            EnsureNotDisposed();

            _activeSessionId = focusSessionId;
            _isMonitoring = true;
            ResetGracePeriodInternal();

            _monitoringCts?.Cancel();
            _monitoringCts?.Dispose();
            _monitoringCts = new CancellationTokenSource();

            var token = _monitoringCts.Token;
            _monitoringTask = Task.Run(() => MonitoringLoopAsync(token), token);
        }
    }

    public void StopMonitoring()
    {
        CancellationTokenSource? ctsToCancel;
        lock (_stateLock)
        {
            _isMonitoring = false;
            _activeSessionId = null;
            ResetGracePeriodInternal();

            ctsToCancel = _monitoringCts;
            _monitoringCts = null;
        }

        if (ctsToCancel is not null)
        {
            try
            {
                ctsToCancel.Cancel();
                ctsToCancel.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void ResetGracePeriod()
    {
        lock (_stateLock)
        {
            ResetGracePeriodInternal();
        }
    }

    private void ResetGracePeriodInternal()
    {
        _currentBlockedTargetKey = null;
        _foregroundEnteredUtc = null;
    }

    public Task<BlockingInterventionResult> EvaluateForegroundAppAsync(CancellationToken cancellationToken = default)
    {
        Guid? sessionId;
        lock (_stateLock)
        {
            sessionId = _activeSessionId;
        }

        if (!sessionId.HasValue)
        {
            return Task.FromResult(BlockingInterventionResult.NoActiveSession());
        }

        return EvaluateForegroundAppAsync(sessionId.Value, cancellationToken);
    }

    public async Task<BlockingInterventionResult> EvaluateForegroundAppAsync(Guid focusSessionId, CancellationToken cancellationToken = default)
    {
        // Monitoring and Agent orchestration may both request an evaluation. Repositories
        // share the Agent DbContext, which is not safe for concurrent operations.
        await _evaluationGate.WaitAsync(cancellationToken);
        try
        {
            return await EvaluateForegroundAppCoreAsync(focusSessionId, cancellationToken);
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    private async Task<BlockingInterventionResult> EvaluateForegroundAppCoreAsync(Guid focusSessionId, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();

        // 1. Lifecycle check: active focus session required (BS-APP-013)
        var session = await _sessionRepo.FindAsync(focusSessionId, cancellationToken);
        if (session is null || !session.IsActive || session.IsTerminal)
        {
            StopMonitoring();
            return BlockingInterventionResult.NoActiveSession();
        }

        // The Flexible profile deliberately does not block configured applications.
        if (string.Equals(session.ProfileCode, BuiltInProfiles.FlexibleCode, StringComparison.OrdinalIgnoreCase))
        {
            ResetGracePeriod();
            return BlockingInterventionResult.NotBlocked(null);
        }

        // 2. Foreground window detection via Win32 API abstraction
        var hWnd = _windowInterop.GetForegroundWindowHandle();
        if (hWnd == 0)
        {
            ResetGracePeriod();
            return BlockingInterventionResult.NoForegroundWindow();
        }

        var pid = _windowInterop.GetWindowProcessId(hWnd);
        if (pid <= 0)
        {
            ResetGracePeriod();
            return BlockingInterventionResult.NoForegroundWindow();
        }

        var (processName, executablePath) = _windowInterop.GetProcessDetails(pid);
        if (string.IsNullOrWhiteSpace(processName))
        {
            ResetGracePeriod();
            return BlockingInterventionResult.NotBlocked(processName, executablePath, pid, hWnd);
        }

        // Protected system, accessibility, and product processes are transparent
        // bypasses regardless of configured rules, exceptions, or grace-period state.
        if (BlockedApp.IsProtectedTarget(executablePath, processName))
        {
            ResetGracePeriod();
            return BlockingInterventionResult.ProtectedTargetBypass(processName, executablePath, pid, hWnd);
        }

        // 3. Allowed applications check
        if (IsApplicationAllowed(processName, executablePath))
        {
            ResetGracePeriod();
            return BlockingInterventionResult.AllowedApp(processName, executablePath, pid, hWnd);
        }

        // 4. Active session exceptions check
        var now = _clock.UtcNow;
        var exceptions = await _exceptionRepo.ListBySessionAsync(session.Id, cancellationToken);
        if (HasActiveException(exceptions, processName, executablePath, now))
        {
            ResetGracePeriod();
            return BlockingInterventionResult.ExceptionActive(processName, executablePath, pid, hWnd);
        }

        // 5. Blocked application rules check
        var rules = await _blockedAppRepo.ListAsync(session.LocalProfileId, cancellationToken);
        var matchedRule = rules.FirstOrDefault(r => r.IsEnabled && MatchesRule(r, processName, executablePath));
        if (matchedRule is null)
        {
            ResetGracePeriod();
            return BlockingInterventionResult.NotBlocked(processName, executablePath, pid, hWnd);
        }

        // 6. Grace period tracking (BS-APP-004: 3-second continuous foreground requirement)
        TimeSpan duration;
        var targetKey = matchedRule.NormalizedExecutableName;
        lock (_stateLock)
        {
            if (_currentBlockedTargetKey != targetKey)
            {
                _currentBlockedTargetKey = targetKey;
                _foregroundEnteredUtc = now;
            }
            else if (_foregroundEnteredUtc.HasValue && now < _foregroundEnteredUtc.Value)
            {
                // Wall-clock rollback must not create a negative grace interval or a
                // false immediate intervention. Restart the continuous-observation window.
                _foregroundEnteredUtc = now;
            }

            duration = now - _foregroundEnteredUtc!.Value;
            if (duration < _options.GracePeriod)
            {
                return BlockingInterventionResult.GracePeriodActive(
                    processName,
                    executablePath,
                    duration,
                    matchedRule.DisplayName,
                    pid,
                    hWnd);
            }
        }

        // 7. Revalidation & Protection check (BS-APP-007, ADR-0003)
        // Immediately before any intervention, revalidate against ExcludedExecutableNames and EnsureTargetCanBeBlocked
        try
        {
            BlockedApp.EnsureTargetCanBeBlocked(executablePath, processName);
        }
        catch (DomainException)
        {
            ResetGracePeriod();
            return BlockingInterventionResult.ProtectedTargetBypass(processName, executablePath, pid, hWnd);
        }

        // ADR-0003 permits a visible overlay only. Request the Desktop overlay and
        // do not call the target blocked until the Desktop acknowledges it is visible.
        var intervention = BlockingInterventionResult.OverlayRequired(
            processName,
            executablePath,
            duration,
            matchedRule.DisplayName,
            pid,
            hWnd);
        InterventionOccurred?.Invoke(this, intervention);
        return intervention;
    }

    private bool IsApplicationAllowed(string processName, string? executablePath)
    {
        lock (_stateLock)
        {
            if (_allowedApps.Count == 0)
            {
                return false;
            }

            if (_allowedApps.Contains(processName))
            {
                return true;
            }

            if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && _allowedApps.Contains(processName + ".exe"))
            {
                return true;
            }

            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && _allowedApps.Contains(processName[..^4]))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(executablePath) && _allowedApps.Contains(executablePath))
            {
                return true;
            }

            return false;
        }
    }

    private static bool HasActiveException(
        IReadOnlyList<SessionException> exceptions,
        string processName,
        string? executablePath,
        DateTime now)
    {
        foreach (var ex in exceptions)
        {
            if (ex.TargetType != ExceptionTargetType.Application || ex.WasAutoExpired)
            {
                continue;
            }

            if (now < ex.GrantedAtUtc || now >= ex.ExpiresAtUtc)
            {
                continue;
            }

            if (string.Equals(ex.TargetId, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ex.TargetId, processName + ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ex.TargetId, processName[..^4], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(executablePath)
                && string.Equals(ex.TargetId, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var targetFileName = Path.GetFileName(ex.TargetId);
            if (!string.IsNullOrWhiteSpace(targetFileName))
            {
                if (string.Equals(targetFileName, processName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetFileName, processName + ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesRule(BlockedApp rule, string processName, string? executablePath)
    {
        if (!rule.IsEnabled)
        {
            return false;
        }

        // A path-specific rule must match the validated executable path exactly.
        // Do not fall back to the basename when the observed path differs or is
        // inaccessible; that would block a different executable with the same name.
        if (!string.IsNullOrWhiteSpace(rule.NormalizedExecutablePath))
        {
            return !string.IsNullOrWhiteSpace(executablePath)
                && string.Equals(rule.NormalizedExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase);
        }

        // Name-only rules remain supported as explicitly configured by the user.
        var ruleName = rule.NormalizedExecutableName;
        if (string.Equals(ruleName, processName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ruleName, processName + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ruleName, processName[..^4], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task RecordEnforcementFailedAsync(Guid sessionId, string processName, CancellationToken cancellationToken)
    {
        if (_eventRepo is null)
        {
            return;
        }

        try
        {
            var now = _clock.UtcNow;
            var sequence = await _eventRepo.MaxSequenceAsync(SessionType.Focus, sessionId, cancellationToken) + 1;
            var localDate = _clock.ToLocalDate(now);
            var payload = JsonSerializer.Serialize(new
            {
                target = processName,
                failureReason = "Visible blocking overlay is not connected; no process action was taken."
            });
            var failureEvent = SessionEvent.Create(
                SessionType.Focus,
                sessionId,
                sequence,
                SessionEventType.EnforcementFailed,
                now,
                localDate,
                _clock.UtcOffsetMinutes,
                payload,
                now);
            await _eventRepo.AddAsync(failureEvent, cancellationToken);
        }
        catch
        {
            // Event recording failure must not mask enforcement outcome
        }
    }

    private async Task MonitoringLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollingInterval, cancellationToken);
                await EvaluateForegroundAppAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Background polling errors must not crash the worker
            }
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsApplicationBlockingEngine));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMonitoring();

        // The monitoring loop may still be finishing a repository operation after
        // cancellation. Dispose synchronously waits so shared repositories/DbContext
        // are not disposed while that operation is in flight.
        try
        {
            _monitoringTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMonitoring();

        if (_monitoringTask is not null)
        {
            try
            {
                await _monitoringTask;
            }
            catch
            {
            }
        }
    }
}
