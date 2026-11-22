using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;
using System.Windows.Threading;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// Status of the background agent service connection.
/// </summary>
public enum AgentConnectionStatus
{
    Connecting,
    Connected,
    OfflineOrDegraded
}

/// <summary>
/// Root ViewModel for the WPF desktop main window.
/// Manages navigation between views (Home, Subjects, Tasks, Focus Setup, Active Session, History, Recovery),
/// tracks background agent connectivity status with retry capability, and performs startup checks
/// for pending interrupted focus sessions awaiting recovery (SCR-016).
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, INavigationService, IAsyncDisposable
{
    private readonly IAgentClient _agentClient;
    private readonly IFocusClient _focusClient;
    private readonly IRecoveryClient _recoveryClient;
    private readonly ILocalClock _clock;
    private readonly bool _ownsClient;

    private ViewModelBase _currentViewModel = null!;
    private string _currentViewTitle = "Home Overview";

    private AgentConnectionStatus _connectionStatus = AgentConnectionStatus.Connecting;
    private string _connectionStatusText = "Connecting to agent...";
    private bool _hasPendingRecovery;
    private string? _pendingRecoveryNotice;
    private string _protectionStatusText = "Protection: checking status…";
    private readonly DispatcherTimer _protectionStatusTimer;
    private readonly DispatcherTimer _applicationInterventionTimer;
    private bool _isPollingApplicationIntervention;

    public MainWindowViewModel(
        IAgentClient? agentClient = null,
        IFocusClient? focusClient = null,
        IRecoveryClient? recoveryClient = null,
        ILocalClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;

        if (agentClient is not null)
        {
            _agentClient = agentClient;
            _ownsClient = false;
        }
        else if (focusClient is NamedPipeFocusClient npfc)
        {
            _agentClient = npfc.AgentClient;
            _ownsClient = false;
        }
        else if (recoveryClient is NamedPipeRecoveryClient nprc)
        {
            _agentClient = nprc.AgentClient;
            _ownsClient = false;
        }
        else
        {
            _agentClient = new StudyAgentClient();
            _ownsClient = true;
        }

        _recoveryClient = recoveryClient ?? _agentClient;
        _focusClient = focusClient ?? new NamedPipeFocusClient(_agentClient);

        NavigateHomeCommand = new RelayCommand(NavigateToHome);
        NavigateSubjectsCommand = new RelayCommand(NavigateToSubjects);
        NavigateTasksCommand = new RelayCommand(NavigateToTasks);
        NavigateFocusCommand = new RelayCommand(NavigateToFocusSetup);
        NavigateHistoryCommand = new RelayCommand(NavigateToHistory);
        NavigateRecoveryCommand = new AsyncRelayCommand(ShowRecoveryDialogAsync);
        RetryConnectionCommand = new AsyncRelayCommand(RetryConnectionAsync, () => ConnectionStatus != AgentConnectionStatus.Connecting);
        OpenRecoveryCommand = new AsyncRelayCommand(ShowRecoveryDialogAsync);

        _protectionStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _protectionStatusTimer.Tick += async (_, _) => await RefreshProtectionStatusAsync();
        _protectionStatusTimer.Start();

        _applicationInterventionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _applicationInterventionTimer.Tick += async (_, _) => await PollApplicationInterventionAsync();

        // Start at Home
        NavigateToHome();
    }

    public MainWindowViewModel(
        IFocusClient? focusClient,
        IRecoveryClient? recoveryClient = null,
        ILocalClock? clock = null)
        : this(null, focusClient, recoveryClient, clock)
    {
    }

    /// <summary>
    /// Event invoked when a modal recovery dialog needs to be presented to the user.
    /// Handled by MainWindow.xaml.cs to instantiate and show <see cref="Views.RecoveryDialog"/>.
    /// </summary>
    public event Func<RecoveryDialogViewModel, Task<bool?>>? ShowRecoveryDialogRequested;

    /// <summary>Updates the real Agent-confirmed blocking overlay state; null closes it.</summary>
    public event Action<ApplicationInterventionDto?>? ApplicationInterventionUpdated;

    public RelayCommand NavigateHomeCommand { get; }

    internal IAgentClient AgentClient => _agentClient;

    public RelayCommand NavigateSubjectsCommand { get; }

    public RelayCommand NavigateTasksCommand { get; }

    public RelayCommand NavigateFocusCommand { get; }

    public RelayCommand NavigateHistoryCommand { get; }

    public AsyncRelayCommand NavigateRecoveryCommand { get; }

    public AsyncRelayCommand RetryConnectionCommand { get; }

    public AsyncRelayCommand OpenRecoveryCommand { get; }

    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                UpdateApplicationInterventionPolling();
            }
        }
    }

    public string CurrentViewTitle
    {
        get => _currentViewTitle;
        private set => SetProperty(ref _currentViewTitle, value);
    }

    public AgentConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsOfflineOrDegraded));
                RetryConnectionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    public string ProtectionStatusText
    {
        get => _protectionStatusText;
        private set => SetProperty(ref _protectionStatusText, value);
    }

    public bool IsConnected => ConnectionStatus == AgentConnectionStatus.Connected;

    public bool IsOfflineOrDegraded => ConnectionStatus == AgentConnectionStatus.OfflineOrDegraded;

    public bool HasPendingRecovery
    {
        get => _hasPendingRecovery;
        private set => SetProperty(ref _hasPendingRecovery, value);
    }

    public string? PendingRecoveryNotice
    {
        get => _pendingRecoveryNotice;
        private set => SetProperty(ref _pendingRecoveryNotice, value);
    }

    /// <summary>
    /// Initializes application state, checks agent connectivity, and detects pending recovery.
    /// </summary>
    public async Task InitializeAsync()
    {
        await CheckConnectionAndRecoveryAsync(launchDialogIfPending: true);
    }

    public async Task RetryConnectionAsync()
    {
        await CheckConnectionAndRecoveryAsync(launchDialogIfPending: false);
    }

    private async Task CheckConnectionAndRecoveryAsync(bool launchDialogIfPending)
    {
        ConnectionStatus = AgentConnectionStatus.Connecting;
        ConnectionStatusText = "Connecting to agent...";

        try
        {
            var isOnline = await _focusClient.PingAgentAsync();
            var recoveryState = await _recoveryClient.GetStateAsync();

            if (!recoveryState.Success && !isOnline)
            {
                ConnectionStatus = AgentConnectionStatus.OfflineOrDegraded;
                ConnectionStatusText = "Agent Offline / Degraded (Using local cache)";
                HasPendingRecovery = false;
                PendingRecoveryNotice = null;
                return;
            }

            ConnectionStatus = AgentConnectionStatus.Connected;
            ConnectionStatusText = "Agent Connected";
            await RefreshProtectionStatusAsync();

            if (recoveryState.State?.Pending is { } pending)
            {
                HasPendingRecovery = true;
                PendingRecoveryNotice =
                    $"Interrupted focus session ({pending.ProfileCode}, {pending.ElapsedSeconds / 60}m elapsed) awaits resolution.";

                if (launchDialogIfPending && !pending.IsExpired)
                {
                    await ShowRecoveryDialogAsync();
                }
            }
            else
            {
                HasPendingRecovery = false;
                PendingRecoveryNotice = null;
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = AgentConnectionStatus.OfflineOrDegraded;
            ConnectionStatusText = $"Agent Unavailable ({ex.Message})";
            ProtectionStatusText = "Protection: Unavailable (Agent offline)";
            HasPendingRecovery = false;
            PendingRecoveryNotice = null;
        }
    }

    private async Task RefreshProtectionStatusAsync()
    {
        if (ConnectionStatus != AgentConnectionStatus.Connected)
        {
            ProtectionStatusText = "Protection: Unavailable (Agent offline)";
            return;
        }

        try
        {
            var state = await _agentClient.GetProtectionStatusAsync();
            var details = string.IsNullOrWhiteSpace(state.DegradedReason) ? string.Empty : $" — {state.DegradedReason}";
            ProtectionStatusText = $"Protection: {state.Status} (apps {state.BlockedAppCount}, sites {state.BlockedSiteCount}){details}";
        }
        catch
        {
            ProtectionStatusText = "Protection: Unavailable (status could not be verified)";
        }
    }

    private void UpdateApplicationInterventionPolling()
    {
        if (CurrentViewModel is ActiveFocusViewModel active && !active.IsFinished)
        {
            _applicationInterventionTimer.Start();
        }
        else
        {
            _applicationInterventionTimer.Stop();
            ApplicationInterventionUpdated?.Invoke(null);
        }
    }

    private async Task PollApplicationInterventionAsync()
    {
        if (_isPollingApplicationIntervention) return;
        if (ConnectionStatus != AgentConnectionStatus.Connected
            || CurrentViewModel is not ActiveFocusViewModel active
            || active.IsFinished)
        {
            ApplicationInterventionUpdated?.Invoke(null);
            return;
        }

        _isPollingApplicationIntervention = true;
        try
        {
            var intervention = await _agentClient.GetApplicationInterventionAsync();
            if (intervention is null || string.Equals(intervention.State, "None", StringComparison.OrdinalIgnoreCase))
            {
                ApplicationInterventionUpdated?.Invoke(null);
            }
            else
            {
                ApplicationInterventionUpdated?.Invoke(intervention);
            }
        }
        catch
        {
            // On loss of the Agent connection, close the local overlay. The Agent owns
            // enforcement truth and must never be represented as protected while offline.
            ConnectionStatus = AgentConnectionStatus.OfflineOrDegraded;
            ConnectionStatusText = "Agent unavailable — protection cannot be verified";
            ProtectionStatusText = "Protection: Unavailable (Agent offline)";
            ApplicationInterventionUpdated?.Invoke(null);
        }
        finally
        {
            _isPollingApplicationIntervention = false;
        }
    }

    #region INavigationService Implementation

    public void NavigateToHome()
    {
        var vm = new HomeViewModel(_focusClient, _recoveryClient, this);
        CurrentViewTitle = "Home Overview";
        CurrentViewModel = vm;
        _ = vm.InitializeAsync();
    }

    public void NavigateToFocusSetup()
    {
        var vm = new FocusSetupViewModel(_focusClient, _recoveryClient, this);
        CurrentViewTitle = "Focus Session Setup";
        CurrentViewModel = vm;
        _ = vm.InitializeAsync();
    }

    public void NavigateToActiveSession(FocusSessionSummary session)
    {
        var vm = new ActiveFocusViewModel(session, _focusClient, this);
        CurrentViewTitle = "Active Focus Session";
        CurrentViewModel = vm;
    }

    public void NavigateToHistory()
    {
        var vm = new SessionHistoryViewModel(_focusClient, this);
        CurrentViewTitle = "Session History";
        CurrentViewModel = vm;
        _ = vm.LoadHistoryAsync();
    }

    public void NavigateToSubjects()
    {
        var vm = new SubjectsViewModel(_agentClient, this);
        CurrentViewTitle = "Subjects";
        CurrentViewModel = vm;
        _ = vm.LoadSubjectsAsync();
    }

    public void NavigateToTasks()
    {
        var vm = new TasksViewModel(_agentClient, this);
        CurrentViewTitle = "Tasks";
        CurrentViewModel = vm;
        _ = vm.LoadTasksAsync();
    }

    public void NavigateToRecovery()
    {
        var vm = new RecoveryDialogViewModel(_recoveryClient, _clock);
        CurrentViewTitle = "Session Recovery";
        CurrentViewModel = vm;
        _ = vm.InitializeAsync();
    }

    public async Task ShowRecoveryDialogAsync()
    {
        var dialogVm = new RecoveryDialogViewModel(_recoveryClient, _clock);
        await dialogVm.InitializeAsync();

        if (ShowRecoveryDialogRequested is not null)
        {
            await ShowRecoveryDialogRequested.Invoke(dialogVm);
        }
        else
        {
            // Headless / fallback: navigate to recovery view
            NavigateToRecovery();
        }

        // Re-check recovery status and refresh current view after dialog closes
        await CheckConnectionAndRecoveryAsync(launchDialogIfPending: false);
        if (CurrentViewModel is HomeViewModel homeVm)
        {
            await homeVm.InitializeAsync();
        }
        else if (CurrentViewModel is FocusSetupViewModel setupVm)
        {
            await setupVm.CheckPendingRecoveryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _protectionStatusTimer.Stop();
        _applicationInterventionTimer.Stop();
        ApplicationInterventionUpdated?.Invoke(null);
        if (_ownsClient && _agentClient is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    #endregion
}
