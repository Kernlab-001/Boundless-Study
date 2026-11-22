using BoundlessStudy.Application.UseCases;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Navigation contract for switching active workspaces and views within MainWindow.
/// </summary>
public interface INavigationService
{
    void NavigateToHome();

    void NavigateToFocusSetup();

    void NavigateToActiveSession(FocusSessionSummary session);

    void NavigateToHistory();

    void NavigateToSubjects();

    void NavigateToTasks();

    void NavigateToRecovery();

    Task ShowRecoveryDialogAsync();
}
