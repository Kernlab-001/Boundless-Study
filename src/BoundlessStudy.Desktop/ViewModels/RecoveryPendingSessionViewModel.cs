using System.Globalization;
using BoundlessStudy.Contracts.Recovery;

namespace BoundlessStudy.Desktop.ViewModels;

/// <summary>
/// Formats and exposes recovery session data for the presentation layer.
/// </summary>
public sealed class RecoveryPendingSessionViewModel : ViewModelBase
{
    private TimeSpan _timeRemaining;
    private bool _isExpired;
    private string _timeRemainingDisplay = string.Empty;

    public RecoveryPendingSessionViewModel(RecoveryPendingSession session, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);

        SessionId = session.SessionId;
        ProfileCode = session.ProfileCode;
        TaskTitle = string.IsNullOrWhiteSpace(session.TaskTitle) ? null : session.TaskTitle;
        StartedAtUtc = session.StartedAtUtc;
        InterruptedAtUtc = session.InterruptedAtUtc;
        ExpiresAtUtc = session.ExpiresAtUtc;
        PlannedFocusSeconds = session.PlannedFocusSeconds;
        ElapsedSeconds = session.ElapsedSeconds;
        ActiveStudySeconds = session.ActiveStudySeconds;

        PlannedDurationDisplay = FormatDurationMinutes(PlannedFocusSeconds);
        ElapsedFocusDisplay = FormatTimeSpanSeconds(ElapsedSeconds);
        ActiveStudyDisplay = FormatTimeSpanSeconds(ActiveStudySeconds);

        var effectiveInterruptionUtc = InterruptedAtUtc ?? StartedAtUtc;
        InterruptionTimestampDisplay = FormatDateTimeBoth(effectiveInterruptionUtc);
        ExpiresAtDisplay = FormatDateTimeBoth(ExpiresAtUtc);

        UpdateRemainingTime(nowUtc);
    }

    public Guid SessionId { get; }

    public string ProfileCode { get; }

    public string? TaskTitle { get; }

    public bool HasTaskTitle => !string.IsNullOrEmpty(TaskTitle);

    public string TaskTitleDisplay => TaskTitle ?? "General Focus (No task assigned)";

    public DateTime StartedAtUtc { get; }

    public DateTime? InterruptedAtUtc { get; }

    public DateTime ExpiresAtUtc { get; }

    public int PlannedFocusSeconds { get; }

    public int ElapsedSeconds { get; }

    public int ActiveStudySeconds { get; }

    public string PlannedDurationDisplay { get; }

    public string ElapsedFocusDisplay { get; }

    public string ActiveStudyDisplay { get; }

    public string InterruptionTimestampDisplay { get; }

    public string ExpiresAtDisplay { get; }

    public TimeSpan TimeRemaining
    {
        get => _timeRemaining;
        private set => SetProperty(ref _timeRemaining, value);
    }

    public string TimeRemainingDisplay
    {
        get => _timeRemainingDisplay;
        private set => SetProperty(ref _timeRemainingDisplay, value);
    }

    public bool IsExpired
    {
        get => _isExpired;
        private set
        {
            if (SetProperty(ref _isExpired, value))
            {
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(StatusDescription));
            }
        }
    }

    public bool CanResume => !IsExpired;

    public string StatusDescription => IsExpired
        ? "This session has exceeded the 24-hour recovery window and will be auto-abandoned. It cannot be resumed."
        : "This session can be resumed to continue your study with preserved focus time.";

    public void UpdateRemainingTime(DateTime nowUtc)
    {
        var remaining = ExpiresAtUtc - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            TimeRemaining = TimeSpan.Zero;
            IsExpired = true;
            TimeRemainingDisplay = "Expired (Auto-abandonment pending)";
        }
        else
        {
            TimeRemaining = remaining;
            IsExpired = false;

            if (remaining.TotalHours >= 1)
            {
                TimeRemainingDisplay = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}h {1:D2}m {2:D2}s remaining",
                    (int)remaining.TotalHours,
                    remaining.Minutes,
                    remaining.Seconds);
            }
            else
            {
                TimeRemainingDisplay = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}m {1:D2}s remaining",
                    remaining.Minutes,
                    remaining.Seconds);
            }
        }
    }

    private static string FormatDurationMinutes(int totalSeconds)
    {
        var minutes = totalSeconds / 60;
        var remainderSeconds = totalSeconds % 60;
        if (remainderSeconds == 0)
        {
            return $"{minutes} minutes ({minutes:D2}:00)";
        }

        return $"{minutes}m {remainderSeconds}s ({totalSeconds}s)";
    }

    private static string FormatTimeSpanSeconds(int totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (ts.TotalHours >= 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}h {1:D2}m {2:D2}s ({3:N0}s)",
                (int)ts.TotalHours,
                ts.Minutes,
                ts.Seconds,
                totalSeconds);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2} ({2:N0}s)",
            ts.Minutes,
            ts.Seconds,
            totalSeconds);
    }

    private static string FormatDateTimeBoth(DateTime utcDateTime)
    {
        var localTime = utcDateTime.Kind == DateTimeKind.Utc ? utcDateTime.ToLocalTime() : utcDateTime;
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:yyyy-MM-dd HH:mm:ss} Local ({1:HH:mm:ss} UTC)",
            localTime,
            utcDateTime);
    }
}
