using System.Collections.ObjectModel;
using System.Globalization;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// ViewModel representing an individual session item in the history list.
/// </summary>
public sealed class FocusSessionHistoryItemViewModel : ViewModelBase
{
    public FocusSessionHistoryItemViewModel(FocusSessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Id = summary.Id;
        ProfileCode = summary.ProfileCode;
        PlannedFocusSeconds = summary.PlannedFocusSeconds;
        ElapsedSeconds = summary.ElapsedSeconds;
        ActiveStudySeconds = summary.ActiveStudySeconds;
        State = summary.State;
        StartedAtUtc = summary.StartedAtUtc;
        EndedAtUtc = summary.EndedAtUtc;
        TaskTitleSnapshot = summary.TaskTitleSnapshot;
        SubjectNameSnapshot = summary.SubjectNameSnapshot;
        XpEarned = summary.XpEarned;

        StartedAtDisplay = FormatDateTimeBoth(summary.StartedAtUtc);
        DurationDisplay = $"{summary.ElapsedSeconds / 60}m elapsed ({summary.PlannedFocusSeconds / 60}m planned, {summary.ActiveStudySeconds / 60}m active)";
        XpEarnedDisplay = summary.XpEarned > 0 ? $"+{summary.XpEarned} XP" : "0 XP";
        TaskTitleDisplay = summary.TaskTitleSnapshot ?? "General Focus";
        SubjectNameDisplay = summary.SubjectNameSnapshot ?? "General";
    }

    public Guid Id { get; }

    public string ProfileCode { get; }

    public int PlannedFocusSeconds { get; }

    public int ElapsedSeconds { get; }

    public int ActiveStudySeconds { get; }

    public SessionState State { get; }

    public DateTime StartedAtUtc { get; }

    public DateTime? EndedAtUtc { get; }

    public string? TaskTitleSnapshot { get; }

    public string? SubjectNameSnapshot { get; }

    public int XpEarned { get; }

    public string StartedAtDisplay { get; }

    public string DurationDisplay { get; }

    public string XpEarnedDisplay { get; }

    public string TaskTitleDisplay { get; }

    public string SubjectNameDisplay { get; }

    public string StateDisplay => State switch
    {
        SessionState.Completed => "Completed",
        SessionState.EndedEarly => "Ended Early",
        SessionState.Abandoned => "Abandoned",
        SessionState.RecoverableInterrupted => "Interrupted",
        SessionState.Active => "Active",
        _ => State.ToString()
    };

    private static string FormatDateTimeBoth(DateTime utcDateTime)
    {
        var localTime = utcDateTime.Kind == DateTimeKind.Utc ? utcDateTime.ToLocalTime() : utcDateTime;
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:yyyy-MM-dd HH:mm:ss}",
            localTime);
    }
}

/// <summary>
/// ViewModel for displaying recent focus session history (§5).
/// Lists past sessions with outcome, duration, XP earned, and task links.
/// </summary>
public sealed class SessionHistoryViewModel : ViewModelBase
{
    private readonly IFocusClient _focusClient;
    private readonly INavigationService _navigation;

    private bool _isLoading;
    private string? _errorMessage;

    public SessionHistoryViewModel(
        IFocusClient focusClient,
        INavigationService navigation)
    {
        _focusClient = focusClient ?? throw new ArgumentNullException(nameof(focusClient));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));

        Sessions = new ObservableCollection<FocusSessionHistoryItemViewModel>();
        RefreshCommand = new AsyncRelayCommand(LoadHistoryAsync, () => !IsLoading);
        BackCommand = new RelayCommand(() => _navigation.NavigateToHome());
    }

    public ObservableCollection<FocusSessionHistoryItemViewModel> Sessions { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand BackCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
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

    public bool IsEmpty => !IsLoading && Sessions.Count == 0;

    public async Task LoadHistoryAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var summaries = await _focusClient.ListSessionHistoryAsync();
            Sessions.Clear();
            foreach (var summary in summaries)
            {
                Sessions.Add(new FocusSessionHistoryItemViewModel(summary));
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unable to load session history: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
