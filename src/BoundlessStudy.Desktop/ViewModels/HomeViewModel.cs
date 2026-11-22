using System.Globalization;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Home dashboard overview (SCR-001, DSH).
/// Displays profile progress, streak stats, active session status card,
/// recovery alert banner, and quick navigation actions.
/// </summary>
public sealed class HomeViewModel : ViewModelBase
{
    private readonly IFocusClient _focusClient;
    private readonly IRecoveryClient _recoveryClient;
    private readonly INavigationService _navigation;

    private int _level = 1;
    private long _totalXp;
    private int _streakDays;
    private int _sessionXpToday;
    private int _remainingDailyCap = EconomyRules.DailySessionXpCap;
    private int _dailyCap = EconomyRules.DailySessionXpCap;
    private bool _isLoading;
    private string? _errorMessage;

    // Active session card properties
    private bool _hasActiveSession;
    private FocusSessionSummary? _activeSession;
    private string _activeSessionElapsedDisplay = "00:00";
    private string _activeSessionPlannedDisplay = "00:00";
    private string _activeSessionProfile = string.Empty;
    private string? _activeSessionTaskTitle;

    // Recovery alert banner properties
    private bool _hasInterruptedSession;
    private string? _interruptedSessionNotice;
    private Guid? _interruptedSessionId;

    public HomeViewModel(
        IFocusClient focusClient,
        IRecoveryClient recoveryClient,
        INavigationService navigation)
    {
        _focusClient = focusClient ?? throw new ArgumentNullException(nameof(focusClient));
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        NavigateFocusSetupCommand = new RelayCommand(() => _navigation.NavigateToFocusSetup());
        NavigateTasksCommand = new RelayCommand(() => _navigation.NavigateToTasks());
        NavigateSubjectsCommand = new RelayCommand(() => _navigation.NavigateToSubjects());
        ViewActiveSessionCommand = new RelayCommand(ViewActiveSession, () => HasActiveSession);
        LaunchRecoveryCommand = new AsyncRelayCommand(LaunchRecoveryAsync, () => HasInterruptedSession);
        RefreshCommand = new AsyncRelayCommand(InitializeAsync, () => !IsLoading);
    }

    public RelayCommand NavigateFocusSetupCommand { get; }

    public RelayCommand NavigateTasksCommand { get; }

    public RelayCommand NavigateSubjectsCommand { get; }

    public RelayCommand ViewActiveSessionCommand { get; }

    public AsyncRelayCommand LaunchRecoveryCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public int Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public long TotalXp
    {
        get => _totalXp;
        private set
        {
            if (SetProperty(ref _totalXp, value))
            {
                OnPropertyChanged(nameof(TotalXpDisplay));
            }
        }
    }

    public string TotalXpDisplay => string.Format(CultureInfo.CurrentCulture, "{0:N0} XP", TotalXp);

    public int StreakDays
    {
        get => _streakDays;
        private set => SetProperty(ref _streakDays, value);
    }

    public int SessionXpToday
    {
        get => _sessionXpToday;
        private set
        {
            if (SetProperty(ref _sessionXpToday, value))
            {
                OnPropertyChanged(nameof(TodayXpDisplay));
            }
        }
    }

    public string TodayXpDisplay => string.Format(CultureInfo.CurrentCulture, "{0} / {1} XP", SessionXpToday, DailyCap);

    public int RemainingDailyCap
    {
        get => _remainingDailyCap;
        private set => SetProperty(ref _remainingDailyCap, value);
    }

    public int DailyCap
    {
        get => _dailyCap;
        private set => SetProperty(ref _dailyCap, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
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

    // Active session card
    public bool HasActiveSession
    {
        get => _hasActiveSession;
        private set
        {
            if (SetProperty(ref _hasActiveSession, value))
            {
                ViewActiveSessionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ActiveSessionElapsedDisplay
    {
        get => _activeSessionElapsedDisplay;
        private set => SetProperty(ref _activeSessionElapsedDisplay, value);
    }

    public string ActiveSessionPlannedDisplay
    {
        get => _activeSessionPlannedDisplay;
        private set => SetProperty(ref _activeSessionPlannedDisplay, value);
    }

    public string ActiveSessionProfile
    {
        get => _activeSessionProfile;
        private set => SetProperty(ref _activeSessionProfile, value);
    }

    public string? ActiveSessionTaskTitle
    {
        get => _activeSessionTaskTitle;
        private set
        {
            if (SetProperty(ref _activeSessionTaskTitle, value))
            {
                OnPropertyChanged(nameof(HasActiveSessionTask));
            }
        }
    }

    public bool HasActiveSessionTask => !string.IsNullOrEmpty(ActiveSessionTaskTitle);

    // Recovery alert banner
    public bool HasInterruptedSession
    {
        get => _hasInterruptedSession;
        private set
        {
            if (SetProperty(ref _hasInterruptedSession, value))
            {
                LaunchRecoveryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? InterruptedSessionNotice
    {
        get => _interruptedSessionNotice;
        private set => SetProperty(ref _interruptedSessionNotice, value);
    }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // 1. Load progress
            var progress = await _focusClient.GetProfileProgressAsync();
            if (progress is not null)
            {
                Level = progress.Level;
                TotalXp = progress.TotalXp;
                StreakDays = progress.CurrentStreakDays;
                SessionXpToday = progress.SessionXpToday;
                RemainingDailyCap = progress.SessionCapRemainingToday;
                DailyCap = progress.DailySessionXpCap;
            }

            // 2. Check active session
            var activeSession = await _focusClient.GetActiveSessionAsync();
            if (activeSession is not null && activeSession.State is SessionState.Active or SessionState.Blocked or SessionState.SystemPaused)
            {
                _activeSession = activeSession;
                HasActiveSession = true;
                ActiveSessionProfile = activeSession.ProfileCode;
                ActiveSessionTaskTitle = activeSession.TaskTitleSnapshot;
                ActiveSessionElapsedDisplay = FormatTimeSpan(activeSession.ElapsedSeconds);
                ActiveSessionPlannedDisplay = FormatTimeSpan(activeSession.PlannedFocusSeconds);
            }
            else
            {
                _activeSession = null;
                HasActiveSession = false;
            }

            // 3. Check interrupted recovery state
            var recoveryState = await _recoveryClient.GetStateAsync();
            if (recoveryState.Success && recoveryState.State?.Pending is { } pending)
            {
                HasInterruptedSession = true;
                _interruptedSessionId = pending.SessionId;
                var taskDesc = string.IsNullOrEmpty(pending.TaskTitle) ? "General Focus" : $"Task: {pending.TaskTitle}";
                InterruptedSessionNotice = $"An interrupted focus session ({pending.ProfileCode}, {taskDesc}) from {pending.StartedAtUtc:g} UTC is awaiting recovery. Resolve it to resume or collect partial XP.";
            }
            else
            {
                HasInterruptedSession = false;
                _interruptedSessionId = null;
                InterruptedSessionNotice = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load dashboard data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ViewActiveSession()
    {
        if (_activeSession is not null)
        {
            _navigation.NavigateToActiveSession(_activeSession);
        }
    }

    private async Task LaunchRecoveryAsync()
    {
        await _navigation.ShowRecoveryDialogAsync();
        await InitializeAsync();
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
}
