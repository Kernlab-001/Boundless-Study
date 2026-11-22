using System.Windows.Threading;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel for the session recovery dialog / view (SCR-016).
/// Mediates between the user and the agent's recovery client over the named pipe.
/// Does not reference BoundlessStudy.Infrastructure or touch SQLite directly.
/// </summary>
public sealed class RecoveryDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IRecoveryClient _recoveryClient;
    private readonly ILocalClock _clock;
    private readonly DispatcherTimer? _timer;

    private RecoveryPendingSessionViewModel? _pendingSession;
    private bool _isLoading;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _noticeMessage;
    private bool? _dialogResult;

    public RecoveryDialogViewModel(
        IRecoveryClient recoveryClient,
        ILocalClock? clock = null,
        bool enableTimer = true)
    {
        _recoveryClient = recoveryClient ?? throw new ArgumentNullException(nameof(recoveryClient));
        _clock = clock ?? SystemClock.Instance;

        ResumeCommand = new AsyncRelayCommand(ResumeSessionAsync, () => CanResume);
        DiscardCommand = new AsyncRelayCommand(DiscardSessionAsync, () => CanDiscard);
        RefreshCommand = new AsyncRelayCommand(LoadStateAsync, () => !IsBusy && !IsLoading);
        CloseCommand = new RelayCommand(RequestCloseDialog);

        if (enableTimer)
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }
    }

    public event EventHandler<bool?>? RequestClose;

    public AsyncRelayCommand ResumeCommand { get; }

    public AsyncRelayCommand DiscardCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RecoveryPendingSessionViewModel? PendingSession
    {
        get => _pendingSession;
        private set
        {
            if (SetProperty(ref _pendingSession, value))
            {
                OnPropertyChanged(nameof(HasPendingSession));
                UpdateCommandStates();
            }
        }
    }

    public bool HasPendingSession => _pendingSession is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                UpdateCommandStates();
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

    public string? NoticeMessage
    {
        get => _noticeMessage;
        private set
        {
            if (SetProperty(ref _noticeMessage, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);

    public bool? DialogResult
    {
        get => _dialogResult;
        private set => SetProperty(ref _dialogResult, value);
    }

    public bool CanResume =>
        !IsBusy &&
        !IsLoading &&
        PendingSession is not null &&
        PendingSession.CanResume;

    public bool CanDiscard =>
        !IsBusy &&
        !IsLoading &&
        PendingSession is not null;

    public async Task InitializeAsync()
    {
        await LoadStateAsync();
    }

    public async Task LoadStateAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var response = await _recoveryClient.GetStateAsync();
            if (!response.Success)
            {
                ErrorMessage = response.ErrorMessage ?? "Could not check for interrupted sessions.";
                PendingSession = null;
                return;
            }

            if (response.State?.Pending is { } pending)
            {
                PendingSession = new RecoveryPendingSessionViewModel(pending, _clock.UtcNow);
            }
            else
            {
                PendingSession = null;
                NoticeMessage = "No interrupted focus session was found. All sessions are clear.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error retrieving recovery state: {ex.Message}";
            PendingSession = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ResumeSessionAsync()
    {
        if (PendingSession is not { } session)
        {
            return;
        }

        if (session.IsExpired)
        {
            ErrorMessage = "This session has expired and can no longer be resumed. It must be discarded.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var response = await _recoveryClient.ResumeAsync(session.SessionId);
            if (!response.Success)
            {
                ErrorMessage = response.ErrorMessage ?? "The agent could not resume the focus session.";
                return;
            }

            NoticeMessage = "Session resumed successfully. Protection rules reapplied.";
            DialogResult = true;
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to resume session: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DiscardSessionAsync()
    {
        if (PendingSession is not { } session)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var response = await _recoveryClient.DiscardAsync(session.SessionId);
            if (!response.Success)
            {
                ErrorMessage = response.ErrorMessage ?? "The agent could not discard the focus session.";
                return;
            }

            NoticeMessage = "Interrupted session discarded. Eligible study time and rewards recorded.";
            DialogResult = false;
            RequestClose?.Invoke(this, false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to discard session: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RequestCloseDialog()
    {
        RequestClose?.Invoke(this, DialogResult);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_pendingSession is not null)
        {
            _pendingSession.UpdateRemainingTime(_clock.UtcNow);
            UpdateCommandStates();
        }
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanDiscard));
        ResumeCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }
    }
}
