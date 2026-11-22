using System.Windows;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.ViewModels;
using BoundlessStudy.Desktop.Views;

namespace BoundlessStudy.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private ApplicationInterventionOverlay? _applicationInterventionOverlay;
    private string? _failedOverlayTargetKey;
    private WindowBoundsDto? _failedOverlayBounds;

    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.ShowRecoveryDialogRequested += OnShowRecoveryDialogRequested;
        _viewModel.ApplicationInterventionUpdated += OnApplicationInterventionUpdated;
        Loaded += OnWindowLoaded;
    }

    private async void OnApplicationInterventionUpdated(ApplicationInterventionDto? intervention)
    {
        if (intervention is null || string.Equals(intervention.State, "None", StringComparison.OrdinalIgnoreCase))
        {
            _failedOverlayTargetKey = null;
            _failedOverlayBounds = null;
            if (_applicationInterventionOverlay is { } current)
            {
                _applicationInterventionOverlay = null;
                current.Close();
            }
            return;
        }

        var targetIdentity = $"{intervention.FocusSessionId}:{intervention.TargetKey}";
        var bounds = intervention.TargetBounds;
        var hasValidBounds = bounds is { Width: > 0 and <= 65536, Height: > 0 and <= 65536 }
            && bounds.X is >= -65536 and <= 65536 && bounds.Y is >= -65536 and <= 65536;
        if (string.Equals(_failedOverlayTargetKey, targetIdentity, StringComparison.Ordinal)
            && _failedOverlayBounds == bounds) return;

        if (!hasValidBounds)
        {
            _failedOverlayTargetKey = targetIdentity;
            _failedOverlayBounds = bounds;
            if (_applicationInterventionOverlay is { } invalidOverlay)
            {
                _applicationInterventionOverlay = null;
                invalidOverlay.Close();
            }
            if (intervention.FocusSessionId is { } invalidSession && !string.IsNullOrWhiteSpace(intervention.TargetKey))
            {
                try
                {
                    await _viewModel.AgentClient.AcknowledgeApplicationOverlayAsync(new ApplicationOverlayAckRequest(
                        invalidSession, intervention.TargetKey, IsVisible: false, ErrorCode: "TargetWindowBoundsUnavailable"));
                }
                catch { }
            }
            return;
        }

        if (string.Equals(_failedOverlayTargetKey, targetIdentity, StringComparison.Ordinal))
        {
            _failedOverlayTargetKey = null;
            _failedOverlayBounds = null;
        }

        if (_applicationInterventionOverlay is null)
        {
            ApplicationInterventionOverlay? overlay = null;
            try
            {
                overlay = new ApplicationInterventionOverlay(_viewModel.AgentClient, intervention)
                {
                    Owner = this,
                    Topmost = true
                };
                _applicationInterventionOverlay = overlay;
                overlay.Closed += (_, _) => _applicationInterventionOverlay = null;
                overlay.Show();
                if (!overlay.IsVisible) throw new InvalidOperationException("WPF did not mark the blocking overlay visible.");
            }
            catch
            {
                _applicationInterventionOverlay = null;
                try { overlay?.Close(); } catch { }
                _failedOverlayTargetKey = targetIdentity;
                _failedOverlayBounds = intervention.TargetBounds;
                if (intervention.FocusSessionId is { } sessionId && !string.IsNullOrWhiteSpace(intervention.TargetKey))
                {
                    try
                    {
                        await _viewModel.AgentClient.AcknowledgeApplicationOverlayAsync(new ApplicationOverlayAckRequest(
                            sessionId, intervention.TargetKey, IsVisible: false, ErrorCode: "DesktopOverlayShowFailed"));
                    }
                    catch { }
                }
            }
            return;
        }

        _applicationInterventionOverlay.Update(intervention);
        if (!_applicationInterventionOverlay.IsVisible)
        {
            _applicationInterventionOverlay.Show();
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private Task<bool?> OnShowRecoveryDialogRequested(RecoveryDialogViewModel dialogVm)
    {
        var dialog = new RecoveryDialog(dialogVm)
        {
            Owner = this
        };

        var result = dialog.ShowDialog();
        return Task.FromResult(result);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _viewModel.ShowRecoveryDialogRequested -= OnShowRecoveryDialogRequested;
        _viewModel.ApplicationInterventionUpdated -= OnApplicationInterventionUpdated;
        _applicationInterventionOverlay?.Close();
        _applicationInterventionOverlay = null;
        if (_viewModel is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        base.OnClosed(e);
    }
}
