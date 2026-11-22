using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BoundlessStudy.Desktop.Accessibility;
using BoundlessStudy.Desktop.ViewModels;

namespace BoundlessStudy.Desktop.Views;

/// <summary>
/// Interaction logic for RecoveryView.xaml with screen-reader announcement integration.
/// </summary>
public partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RecoveryDialogViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(RecoveryDialogViewModel.ErrorMessage) && !string.IsNullOrWhiteSpace(vm.ErrorMessage))
        {
            ScreenReader.Announce(
                this,
                $"Error: {vm.ErrorMessage}",
                AutomationNotificationKind.ActionAborted,
                AutomationNotificationProcessing.ImportantAll,
                "BoundlessStudyRecoveryError");
        }
        else if (e.PropertyName == nameof(RecoveryDialogViewModel.NoticeMessage) && !string.IsNullOrWhiteSpace(vm.NoticeMessage))
        {
            ScreenReader.Announce(
                this,
                vm.NoticeMessage,
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                "BoundlessStudyRecoveryNotice");
        }
        else if (e.PropertyName == nameof(RecoveryDialogViewModel.IsLoading) && vm.IsLoading)
        {
            ScreenReader.Announce(
                this,
                "Communicating with the background agent...",
                AutomationNotificationKind.ItemAdded,
                AutomationNotificationProcessing.MostRecent,
                "BoundlessStudyRecoveryLoading");
        }
    }
}
