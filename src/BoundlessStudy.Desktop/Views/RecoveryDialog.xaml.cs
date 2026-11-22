using System.Windows;
using BoundlessStudy.Desktop.ViewModels;

namespace BoundlessStudy.Desktop.Views;

/// <summary>
/// Interaction logic for RecoveryDialog.xaml
/// </summary>
public partial class RecoveryDialog : Window
{
    public RecoveryDialog()
    {
        InitializeComponent();
    }

    public RecoveryDialog(RecoveryDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, bool? result)
    {
        DialogResult = result;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is RecoveryDialogViewModel vm)
        {
            vm.RequestClose -= OnRequestClose;
        }

        base.OnClosed(e);
    }
}
