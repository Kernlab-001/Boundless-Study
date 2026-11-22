using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>Desktop presentation for an Agent-confirmed application intervention.</summary>
public sealed class ApplicationInterventionOverlayViewModel : ViewModelBase
{
    private readonly IAgentClient _agentClient;
    private ApplicationInterventionDto _intervention;
    private ExceptionReasonCategory _selectedReason = ExceptionReasonCategory.Other;
    private string _statusMessage;
    private bool _isBusy;
    private bool _acknowledged;

    public ApplicationInterventionOverlayViewModel(IAgentClient agentClient, ApplicationInterventionDto intervention)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _intervention = intervention ?? throw new ArgumentNullException(nameof(intervention));
        _statusMessage = CreateStatusMessage(intervention);
        ReasonCategories = Enum.GetValues<ExceptionReasonCategory>();
        RequestExceptionCommand = new AsyncRelayCommand(RequestExceptionAsync, () => !IsBusy && CanRequestException);
    }

    public string ApplicationName => string.IsNullOrWhiteSpace(_intervention.DisplayName)
        ? "Application enforcement unavailable"
        : _intervention.DisplayName;
    public string Reason => string.IsNullOrWhiteSpace(_intervention.Reason)
        ? "This application matches an active focus restriction."
        : _intervention.Reason;
    public string SessionDisplay => _intervention.FocusSessionId is { } id ? $"Session {id:D}" : "No active session could be verified.";
    public string EnforcementStatus => _intervention.State switch
    {
        "OverlayVisible" => "The visible overlay is confirmed by the Agent.",
        "Detected" => "Waiting for the Agent to confirm the visible overlay.",
        "Failed" => "The Agent could not confirm that this restriction is working.",
        _ => "Application protection status is unavailable."
    };
    public IReadOnlyList<ExceptionReasonCategory> ReasonCategories { get; }
    public AsyncRelayCommand RequestExceptionCommand { get; }
    public bool CanRequestException => _intervention.FocusSessionId.HasValue
        && !string.IsNullOrWhiteSpace(_intervention.TargetKey)
        && _intervention.AvailableActions?.Contains("GrantTemporaryException", StringComparer.OrdinalIgnoreCase) == true;

    public ExceptionReasonCategory SelectedReason
    {
        get => _selectedReason;
        set => SetProperty(ref _selectedReason, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RequestExceptionCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task<bool> AcknowledgeVisibleAsync(long overlayWindowHandle)
    {
        if (_acknowledged || !_intervention.FocusSessionId.HasValue || string.IsNullOrWhiteSpace(_intervention.TargetKey)
            || overlayWindowHandle == 0
            || !string.Equals(_intervention.State, "Detected", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_intervention.State, "OverlayVisible", StringComparison.OrdinalIgnoreCase)) return _acknowledged;
        try
        {
            await _agentClient.AcknowledgeApplicationOverlayAsync(new ApplicationOverlayAckRequest(
                _intervention.FocusSessionId.Value,
                _intervention.TargetKey,
                IsVisible: true,
                OverlayWindowHandle: overlayWindowHandle));
            _acknowledged = true;
            StatusMessage = "The overlay is visible. The Agent has been notified.";
            return true;
        }
        catch
        {
            StatusMessage = "The overlay is visible, but the Agent could not verify it. Protection is unavailable until the connection recovers.";
            return false;
        }
    }

    public Task AcknowledgeCoverageFailureAsync(string errorCode)
    {
        if (!_intervention.FocusSessionId.HasValue || string.IsNullOrWhiteSpace(_intervention.TargetKey))
        {
            StatusMessage = "The overlay could not cover the restricted application. Enforcement is unavailable.";
            return Task.CompletedTask;
        }

        return SendCoverageFailureAsync(errorCode);
    }

    private async Task SendCoverageFailureAsync(string errorCode)
    {
        try
        {
            await _agentClient.AcknowledgeApplicationOverlayAsync(new ApplicationOverlayAckRequest(
                _intervention.FocusSessionId!.Value,
                _intervention.TargetKey!,
                IsVisible: false,
                ErrorCode: errorCode));
            _acknowledged = true;
            StatusMessage = "The overlay could not cover the restricted application. Enforcement is unavailable.";
        }
        catch
        {
            StatusMessage = "The overlay could not verify full application coverage or contact the Agent.";
        }
    }

    public bool Update(ApplicationInterventionDto intervention)
    {
        var targetChanged = !string.Equals(_intervention.TargetKey, intervention.TargetKey, StringComparison.Ordinal)
            || _intervention.FocusSessionId != intervention.FocusSessionId;
        _intervention = intervention;
        if (targetChanged) _acknowledged = false;
        OnPropertyChanged(nameof(ApplicationName));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(SessionDisplay));
        OnPropertyChanged(nameof(EnforcementStatus));
        OnPropertyChanged(nameof(CanRequestException));
        RequestExceptionCommand.RaiseCanExecuteChanged();
        if (!_acknowledged) StatusMessage = CreateStatusMessage(intervention);
        return targetChanged;
    }

    private async Task RequestExceptionAsync()
    {
        if (!CanRequestException || !_intervention.FocusSessionId.HasValue) return;
        IsBusy = true;
        StatusMessage = "Requesting a temporary, session-scoped exception…";
        try
        {
            await _agentClient.GrantSessionExceptionAsync(new GrantExceptionRequest(
                _intervention.FocusSessionId.Value,
                ExceptionTargetType.Application.ToString(),
                ApplicationName,
                SelectedReason.ToString()));
            StatusMessage = "Request recorded. The focus profile controls its delay, duration and availability.";
        }
        catch
        {
            StatusMessage = "The Agent could not grant this exception. The restriction remains in effect.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string CreateStatusMessage(ApplicationInterventionDto intervention) =>
        string.Equals(intervention.State, "OverlayVisible", StringComparison.OrdinalIgnoreCase)
            ? "The visible blocking overlay is confirmed by the Agent."
            : string.Equals(intervention.State, "Failed", StringComparison.OrdinalIgnoreCase)
                ? "The Agent could not verify enforcement. Focus protection is unavailable."
                : "The Agent detected this app and is waiting for the visible overlay to connect.";
}
