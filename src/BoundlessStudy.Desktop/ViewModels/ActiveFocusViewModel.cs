using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel for an ongoing active focus session (§5, §6).
/// Manages the live timer ticker, displays active/elapsed/remaining time,
/// handles early exit with reason selection, normal completion, and displays
/// the final session outcome (XP, levels, streak qualification, achievements).
/// </summary>
public sealed class ActiveFocusViewModel : ViewModelBase, IDisposable
{
    private readonly IFocusClient _focusClient;
    private readonly INavigationService _navigation;
    private readonly DispatcherTimer? _timer;
    private readonly DispatcherTimer? _agentSyncTimer;

    private readonly Guid _sessionId;
    private readonly string _profileCode;
    private readonly int _plannedFocusSeconds;
    private readonly string? _taskTitle;
    private readonly string? _subjectName;

    private int _elapsedSeconds;
    private int _activeStudySeconds;
    private int _remainingSeconds;
    private SessionState _sessionState = SessionState.Active;
    private bool _isBusy;
    private string? _errorMessage;

    // Reason selection for End Early
    private FocusEndReason _selectedEndReason = FocusEndReason.UserEnded;

    // Outcome summary
    private bool _isFinished;
    private SessionCompletionOutcome? _outcome;
    private bool _isAgentAvailable = true;
    private bool _isRefreshingAgentState;
    private bool _isWritingCheckpoint;
    private bool _isIdle;
    private int _idleDurationSeconds;
    private long _lastTimerTimestamp;
    private long _lastAgentSyncTimestamp;
    private double _fractionalElapsedSeconds;
    private int _lastCheckpointElapsedSeconds;

    private const int CheckpointIntervalSeconds = 5;
    private static readonly TimeSpan AgentSyncInterval = TimeSpan.FromSeconds(2);

    public ActiveFocusViewModel(
        FocusSessionSummary session,
        IFocusClient focusClient,
        INavigationService navigation,
        bool enableTimer = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        _focusClient = focusClient ?? throw new ArgumentNullException(nameof(focusClient));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        _sessionId = session.Id;
        _profileCode = session.ProfileCode;
        _plannedFocusSeconds = Math.Max(60, session.PlannedFocusSeconds);
        _taskTitle = session.TaskTitleSnapshot;
        _subjectName = session.SubjectNameSnapshot;

        _elapsedSeconds = session.ElapsedSeconds;
        _activeStudySeconds = session.ActiveStudySeconds;
        _remainingSeconds = Math.Max(0, _plannedFocusSeconds - _elapsedSeconds);
        _sessionState = session.State;
        _isIdle = session.IsIdle;
        _idleDurationSeconds = Math.Max(0, session.IdleDurationSeconds);
        _lastCheckpointElapsedSeconds = session.ElapsedSeconds;
        _lastTimerTimestamp = Stopwatch.GetTimestamp();

        AvailableEndReasons =
        [
            FocusEndReason.UserEnded,
            FocusEndReason.Completed,
            FocusEndReason.Unknown
        ];

        CompleteCommand = new AsyncRelayCommand(CompleteSessionAsync, () => CanComplete);
        EndEarlyCommand = new AsyncRelayCommand(EndEarlyAsync, () => CanEndEarly);
        ReturnHomeCommand = new RelayCommand(() => _navigation.NavigateToHome());

        if (enableTimer && _sessionState is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused)
        {
            // The persisted snapshot used to navigate here can be stale by the time
            // this view opens. Verify the live Agent state before counting or enabling actions.
            _isAgentAvailable = false;
            OnPropertyChanged(nameof(StateDisplay));
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            _agentSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _agentSyncTimer.Tick += OnAgentSyncTimerTick;
            _agentSyncTimer.Start();
        }
    }

    public Guid SessionId => _sessionId;

    public string ProfileCode => _profileCode;

    public int PlannedFocusSeconds => _plannedFocusSeconds;

    public string? TaskTitle => _taskTitle;

    public bool HasTaskTitle => !string.IsNullOrEmpty(TaskTitle);

    public string TaskTitleDisplay => TaskTitle ?? "General Focus (No task assigned)";

    public string? SubjectName => _subjectName;

    public bool HasSubjectName => !string.IsNullOrEmpty(SubjectName);

    public string SubjectNameDisplay => SubjectName ?? "General";

    public IReadOnlyList<FocusEndReason> AvailableEndReasons { get; }

    public AsyncRelayCommand CompleteCommand { get; }

    public AsyncRelayCommand EndEarlyCommand { get; }

    public RelayCommand ReturnHomeCommand { get; }

    public int ElapsedSeconds
    {
        get => _elapsedSeconds;
        private set
        {
            if (SetProperty(ref _elapsedSeconds, value))
            {
                OnPropertyChanged(nameof(ElapsedDisplay));
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public string ElapsedDisplay => FormatTimeSpan(ElapsedSeconds);

    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set
        {
            if (SetProperty(ref _remainingSeconds, value))
            {
                OnPropertyChanged(nameof(RemainingDisplay));
            }
        }
    }

    public string RemainingDisplay => FormatTimeSpan(RemainingSeconds);

    public int ActiveStudySeconds
    {
        get => _activeStudySeconds;
        private set
        {
            if (SetProperty(ref _activeStudySeconds, value))
            {
                OnPropertyChanged(nameof(ActiveStudyDisplay));
            }
        }
    }

    public string ActiveStudyDisplay => FormatTimeSpan(ActiveStudySeconds);

    public string PlannedDurationDisplay => FormatDurationMinutes(PlannedFocusSeconds);

    public double ProgressPercent
    {
        get
        {
            if (PlannedFocusSeconds <= 0) return 0;
            return Math.Min(100.0, (ElapsedSeconds * 100.0) / PlannedFocusSeconds);
        }
    }

    public SessionState SessionState
    {
        get => _sessionState;
        private set
        {
            if (SetProperty(ref _sessionState, value))
            {
                OnPropertyChanged(nameof(StateDisplay));
            }
        }
    }

    public string StateDisplay => !_isAgentAvailable
        ? "Agent unavailable — timer paused until session state is verified"
        : SessionState switch
        {
            SessionState.Active when _isIdle => $"Idle beyond the 5-minute threshold ({FormatTimeSpan(_idleDurationSeconds)}); active-study time paused",
            SessionState.Active => "Active (protection status shown in the status bar)",
            SessionState.Blocked => "Distraction Blocked",
            SessionState.SystemPaused => "System Paused",
            SessionState.Completed => "Session Completed",
            SessionState.EndedEarly => "Ended Early",
            SessionState.Abandoned => "Abandoned",
            _ => SessionState.ToString()
        };

    public FocusEndReason SelectedEndReason
    {
        get => _selectedEndReason;
        set => SetProperty(ref _selectedEndReason, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // Summary outcome
    public bool IsFinished
    {
        get => _isFinished;
        private set
        {
            if (SetProperty(ref _isFinished, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                UpdateCommandStates();
            }
        }
    }

    public bool IsRunning => !IsFinished;

    public SessionCompletionOutcome? Outcome
    {
        get => _outcome;
        private set
        {
            if (SetProperty(ref _outcome, value))
            {
                OnPropertyChanged(nameof(HasOutcome));
                OnPropertyChanged(nameof(AwardXp));
                OnPropertyChanged(nameof(AwardXpDisplay));
                OnPropertyChanged(nameof(TotalXp));
                OnPropertyChanged(nameof(TotalXpDisplay));
                OnPropertyChanged(nameof(LevelBefore));
                OnPropertyChanged(nameof(LevelAfter));
                OnPropertyChanged(nameof(LevelChangeDisplay));
                OnPropertyChanged(nameof(StreakQualified));
                OnPropertyChanged(nameof(StreakQualifiedDisplay));
                OnPropertyChanged(nameof(HasUnlockedAchievements));
                OnPropertyChanged(nameof(UnlockedAchievementsDisplay));
            }
        }
    }

    public bool HasOutcome => Outcome is not null;

    public int AwardXp => Outcome?.XpAwarded ?? 0;

    public string AwardXpDisplay => Outcome is not null ? $"+{Outcome.XpAwarded} XP" : "0 XP";

    public long TotalXp => Outcome?.TotalXpAfter ?? 0;

    public string TotalXpDisplay => Outcome is not null ? $"{Outcome.TotalXpAfter:N0} XP" : "0 XP";

    public int LevelBefore => Outcome?.LevelBefore ?? 1;

    public int LevelAfter => Outcome?.LevelAfter ?? 1;

    public bool StreakQualified => Outcome?.IsStreakQualified ?? false;

    public string LevelChangeDisplay
    {
        get
        {
            if (Outcome is null) return string.Empty;
            if (Outcome.LevelAfter > Outcome.LevelBefore)
            {
                return $"Leveled Up! Level {Outcome.LevelBefore} -> Level {Outcome.LevelAfter}";
            }
            return $"Level {Outcome.LevelAfter}";
        }
    }

    public string StreakQualifiedDisplay => Outcome switch
    {
        null => string.Empty,
        { IsStreakQualified: true } => "Qualified for Daily Streak! (+1 Day)",
        _ => "Not streak-qualified (requires >= 20 mins completed)"
    };

    public bool HasUnlockedAchievements => Outcome?.AchievementsUnlocked.Count > 0;

    public string UnlockedAchievementsDisplay => Outcome switch
    {
        null => string.Empty,
        { AchievementsUnlocked.Count: > 0 } => string.Join(", ", Outcome.AchievementsUnlocked),
        _ => "None"
    };

    public bool CanComplete => !IsBusy && !IsFinished && _isAgentAvailable
        && SessionState == SessionState.Active && ElapsedSeconds >= PlannedFocusSeconds;

    public bool CanEndEarly => !IsBusy && !IsFinished && _isAgentAvailable
        && SessionState == SessionState.Active;

    public bool IsIdle => _isIdle;

    public int IdleDurationSeconds => _idleDurationSeconds;

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (IsFinished || !_isAgentAvailable || SessionState != SessionState.Active)
        {
            ResetTickClock();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _fractionalElapsedSeconds += Math.Max(0, now - _lastTimerTimestamp) / (double)Stopwatch.Frequency;
        _lastTimerTimestamp = now;
        var wholeSeconds = (int)Math.Floor(_fractionalElapsedSeconds);
        if (wholeSeconds > 0)
        {
            _fractionalElapsedSeconds -= wholeSeconds;
            ElapsedSeconds += wholeSeconds;
            if (!_isIdle) ActiveStudySeconds += wholeSeconds;
            RemainingSeconds = Math.Max(0, PlannedFocusSeconds - ElapsedSeconds);
            UpdateCommandStates();
        }

        if (ElapsedSeconds - _lastCheckpointElapsedSeconds >= CheckpointIntervalSeconds)
        {
            await WriteCheckpointAsync();
        }
    }

    private async void OnAgentSyncTimerTick(object? sender, EventArgs e)
    {
        if (IsFinished || _isRefreshingAgentState) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastAgentSyncTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastAgentSyncTimestamp, now) < AgentSyncInterval) return;
        _lastAgentSyncTimestamp = now;
        await RefreshAgentStateAsync();
    }

    /// <summary>Refreshes session state from the Agent; public for deterministic UI verification.</summary>
    public async Task RefreshAgentStateAsync(CancellationToken cancellationToken = default)
    {
        if (_isRefreshingAgentState || IsFinished) return;
        _isRefreshingAgentState = true;
        try
        {
            if (!await _focusClient.PingAgentAsync(cancellationToken))
            {
                SetAgentUnavailable();
                return;
            }

            var authoritative = await _focusClient.GetActiveSessionAsync(cancellationToken);
            if (authoritative is null || authoritative.Id != SessionId)
            {
                SetAgentUnavailable();
                return;
            }

            var wasActive = _isAgentAvailable && SessionState == SessionState.Active;
            var wasIdle = _isIdle;
            _isAgentAvailable = true;
            OnPropertyChanged(nameof(StateDisplay));

            var isActive = authoritative.State == SessionState.Active;
            SessionState = authoritative.State;
            _isIdle = authoritative.IsIdle;
            _idleDurationSeconds = Math.Max(0, authoritative.IdleDurationSeconds);
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IdleDurationSeconds));
            if (isActive)
            {
                ElapsedSeconds = Math.Max(ElapsedSeconds, authoritative.ElapsedSeconds);
                ActiveStudySeconds = authoritative.IsIdle && !wasIdle
                    ? authoritative.ActiveStudySeconds
                    : Math.Max(ActiveStudySeconds, authoritative.ActiveStudySeconds);
            }
            else
            {
                // On pause, the Agent's persisted snapshot wins over local ticks that may
                // have raced with the system-state transition.
                ElapsedSeconds = authoritative.ElapsedSeconds;
                ActiveStudySeconds = authoritative.ActiveStudySeconds;
                _lastCheckpointElapsedSeconds = authoritative.ElapsedSeconds;
            }

            RemainingSeconds = Math.Max(0, PlannedFocusSeconds - ElapsedSeconds);
            if (!isActive || !wasActive) ResetTickClock();

            if (isActive && ElapsedSeconds - _lastCheckpointElapsedSeconds >= CheckpointIntervalSeconds)
            {
                await WriteCheckpointAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal view shutdown.
        }
        catch
        {
            SetAgentUnavailable();
        }
        finally
        {
            _isRefreshingAgentState = false;
            UpdateCommandStates();
        }
    }

    private void SetAgentUnavailable()
    {
        if (_isAgentAvailable)
        {
            _isAgentAvailable = false;
            OnPropertyChanged(nameof(StateDisplay));
        }

        ResetTickClock();
        UpdateCommandStates();
    }

    private void ResetTickClock()
    {
        _lastTimerTimestamp = Stopwatch.GetTimestamp();
        _fractionalElapsedSeconds = 0;
    }

    private async Task WriteCheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (_isWritingCheckpoint || !_isAgentAvailable || SessionState != SessionState.Active) return;
        _isWritingCheckpoint = true;
        try
        {
            await _focusClient.ApplyCheckpointAsync(SessionId, ElapsedSeconds, ActiveStudySeconds, cancellationToken);
            _lastCheckpointElapsedSeconds = ElapsedSeconds;
        }
        catch
        {
            SetAgentUnavailable();
        }
        finally
        {
            _isWritingCheckpoint = false;
        }
    }

    private async Task CompleteSessionAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StopTimer();

        try
        {
            var outcome = await _focusClient.CompleteFocusSessionAsync(
                SessionId,
                ElapsedSeconds,
                ActiveStudySeconds,
                0,
                0);

            if (!outcome.Success)
            {
                ErrorMessage = outcome.Error?.Message ?? "Failed to complete focus session.";
                return;
            }

            SessionState = SessionState.Completed;
            Outcome = outcome;
            IsFinished = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error completing session: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (!IsFinished && _isAgentAvailable) StartTimers();
        }
    }

    private async Task EndEarlyAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StopTimer();

        try
        {
            var outcome = await _focusClient.EndFocusEarlyAsync(
                SessionId,
                ElapsedSeconds,
                ActiveStudySeconds,
                0,
                0,
                SelectedEndReason);

            if (!outcome.Success)
            {
                ErrorMessage = outcome.Error?.Message ?? "Failed to end focus session.";
                return;
            }

            SessionState = SessionState.EndedEarly;
            Outcome = outcome;
            IsFinished = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error ending session early: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (!IsFinished && _isAgentAvailable) StartTimers();
        }
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CanEndEarly));
        CompleteCommand.RaiseCanExecuteChanged();
        EndEarlyCommand.RaiseCanExecuteChanged();
    }

    private void StopTimer()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }

        if (_agentSyncTimer is not null)
        {
            _agentSyncTimer.Stop();
            _agentSyncTimer.Tick -= OnAgentSyncTimerTick;
        }
    }

    private void StartTimers()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTimerTick;
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        if (_agentSyncTimer is not null)
        {
            _agentSyncTimer.Tick -= OnAgentSyncTimerTick;
            _agentSyncTimer.Tick += OnAgentSyncTimerTick;
            _agentSyncTimer.Start();
        }

        ResetTickClock();
    }

    public void Dispose()
    {
        StopTimer();
        if (!IsFinished && _isAgentAvailable && SessionState == SessionState.Active)
        {
            _ = WriteCheckpointAsync();
        }
    }

    private static string FormatTimeSpan(int totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (ts.TotalHours >= 1)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", ts.Minutes, ts.Seconds);
    }

    private static string FormatDurationMinutes(int totalSeconds)
    {
        var minutes = totalSeconds / 60;
        return $"{minutes} minutes";
    }
}
