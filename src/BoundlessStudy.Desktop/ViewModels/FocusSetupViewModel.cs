using System.Collections.ObjectModel;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel for configuring and launching a focus session (§5.4, §6).
/// Checks for pending recoverable sessions: BLOCKS new session creation if an unexpired
/// interrupted session exists until user resumes or discards it.
/// </summary>
public sealed class FocusSetupViewModel : ViewModelBase
{
    private readonly IFocusClient _focusClient;
    private readonly IRecoveryClient _recoveryClient;
    private readonly INavigationService _navigation;

    private FocusProfileSummary? _selectedProfile;
    private int _selectedDurationMinutes = 25;
    private StudyTaskSummary? _selectedTask;
    private bool _isLoading;
    private bool _isBusy;
    private string? _errorMessage;

    // Interrupted session blocking state
    private bool _hasPendingRecoverableSession;
    private string? _recoverableSessionWarning;

    public FocusSetupViewModel(
        IFocusClient focusClient,
        IRecoveryClient recoveryClient,
        INavigationService navigation)
    {
        _focusClient = focusClient ?? throw new ArgumentNullException(nameof(focusClient));
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        Profiles = new ObservableCollection<FocusProfileSummary>();
        Tasks = new ObservableCollection<StudyTaskSummary>();
        AvailableDurations = [15, 25, 45, 60, 90];

        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => CanStartSession);
        ResolveRecoveryCommand = new AsyncRelayCommand(ResolveRecoveryAsync, () => HasPendingRecoverableSession);
        RefreshCommand = new AsyncRelayCommand(InitializeAsync, () => !IsBusy && !IsLoading);
        CancelCommand = new RelayCommand(() => _navigation.NavigateToHome());
    }

    public ObservableCollection<FocusProfileSummary> Profiles { get; }

    public ObservableCollection<StudyTaskSummary> Tasks { get; }

    public IReadOnlyList<int> AvailableDurations { get; }

    public AsyncRelayCommand StartSessionCommand { get; }

    public AsyncRelayCommand ResolveRecoveryCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CancelCommand { get; }

    public FocusProfileSummary? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(SelectedProfileDescription));
                UpdateCanStart();
            }
        }
    }

    public string SelectedProfileDescription => SelectedProfile switch
    {
        null => "Select a profile to view rules and XP multiplier.",
        _ => $"{SelectedProfile.DisplayName}: {SelectedProfile.XpPerActiveMinute} XP/min, Early exit: {SelectedProfile.EarlyExitMode}, Max exceptions: {SelectedProfile.MaxExceptionsPerSession}"
    };

    public int SelectedDurationMinutes
    {
        get => _selectedDurationMinutes;
        set
        {
            if (SetProperty(ref _selectedDurationMinutes, value))
            {
                UpdateCanStart();
            }
        }
    }

    public StudyTaskSummary? SelectedTask
    {
        get => _selectedTask;
        set => SetProperty(ref _selectedTask, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                UpdateCanStart();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCanStart();
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

    public bool HasPendingRecoverableSession
    {
        get => _hasPendingRecoverableSession;
        private set
        {
            if (SetProperty(ref _hasPendingRecoverableSession, value))
            {
                UpdateCanStart();
                ResolveRecoveryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? RecoverableSessionWarning
    {
        get => _recoverableSessionWarning;
        private set => SetProperty(ref _recoverableSessionWarning, value);
    }

    public bool CanStartSession =>
        !IsBusy &&
        !IsLoading &&
        !HasPendingRecoverableSession &&
        SelectedProfile is not null &&
        SelectedDurationMinutes > 0;

    public async Task InitializeAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // 1. Check for recoverable interrupted session - MUST block if unexpired exists
            await CheckPendingRecoveryAsync();

            // 2. Load focus profiles
            var profiles = await _focusClient.ListFocusProfilesAsync();
            Profiles.Clear();
            foreach (var p in profiles)
            {
                Profiles.Add(p);
            }

            if (SelectedProfile is null && Profiles.Count > 0)
            {
                SelectedProfile = Profiles.FirstOrDefault(p => p.Code == BuiltInProfiles.BalancedCode) ?? Profiles[0];
            }

            // 3. Load active study tasks
            var tasks = await _focusClient.ListTasksAsync();
            Tasks.Clear();
            foreach (var t in tasks.Where(t => !t.IsDeleted && !t.IsArchived && t.Status != StudyTaskStatus.Done))
            {
                Tasks.Add(t);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load focus setup data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CheckPendingRecoveryAsync()
    {
        var recoveryState = await _recoveryClient.GetStateAsync();
        if (recoveryState.Success && recoveryState.State?.Pending is { } pending)
        {
            if (!pending.IsExpired)
            {
                HasPendingRecoverableSession = true;
                RecoverableSessionWarning =
                    $"Cannot start a new session: an interrupted session ({pending.ProfileCode}, {pending.ElapsedSeconds / 60}m elapsed) " +
                    "is awaiting recovery. You must Resume or Discard it before starting a new focus session.";
                return;
            }
        }

        HasPendingRecoverableSession = false;
        RecoverableSessionWarning = null;
    }

    private async Task StartSessionAsync()
    {
        if (SelectedProfile is null)
        {
            ErrorMessage = "Please select a focus profile.";
            return;
        }

        // Final safety check against pending recoverable session
        await CheckPendingRecoveryAsync();
        if (HasPendingRecoverableSession)
        {
            ErrorMessage = "A recoverable interrupted session is still pending. Please resolve it first.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var plannedSeconds = SelectedDurationMinutes * 60;
            var result = await _focusClient.StartFocusSessionAsync(
                SelectedProfile.Code,
                plannedSeconds,
                SelectedTask?.Id);

            if (!result.Success)
            {
                ErrorMessage = result.Error?.Message ?? "Failed to start focus session.";
                return;
            }

            // Successfully started: build session summary and navigate
            var sessionSummary = new FocusSessionSummary(
                Id: result.Id!.Value,
                ProfileCode: SelectedProfile.Code,
                PlannedFocusSeconds: plannedSeconds,
                ElapsedSeconds: 0,
                ActiveStudySeconds: 0,
                State: SessionState.Active,
                StartedAtUtc: DateTime.UtcNow,
                EndedAtUtc: null,
                TaskTitleSnapshot: SelectedTask?.Title,
                SubjectNameSnapshot: SelectedTask?.SubjectName,
                XpEarned: 0,
                IsInterrupted: false);

            _navigation.NavigateToActiveSession(sessionSummary);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error starting session: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResolveRecoveryAsync()
    {
        await _navigation.ShowRecoveryDialogAsync();
        await CheckPendingRecoveryAsync();
    }

    private void UpdateCanStart()
    {
        OnPropertyChanged(nameof(CanStartSession));
        StartSessionCommand.RaiseCanExecuteChanged();
    }
}
