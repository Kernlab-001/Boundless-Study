using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Desktop.ViewModels;

namespace BoundlessStudy.Desktop.Views;

/// <summary>Topmost, modeless and visible application intervention window.</summary>
public partial class ApplicationInterventionOverlay : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private bool _hasRendered;
    private WindowBoundsDto? _targetBounds;
    private ApplicationInterventionDto _intervention;

    public ApplicationInterventionOverlay(IAgentClient agentClient, ApplicationInterventionDto intervention)
    {
        InitializeComponent();
        DataContext = new ApplicationInterventionOverlayViewModel(agentClient, intervention);
        _intervention = intervention;
        _targetBounds = intervention.TargetBounds;
        ContentRendered += OnContentRendered;
    }

    public async void Update(ApplicationInterventionDto intervention)
    {
        var targetBoundsChanged = _targetBounds != intervention.TargetBounds;
        _targetBounds = intervention.TargetBounds;
        _intervention = intervention;
        if (DataContext is ApplicationInterventionOverlayViewModel viewModel)
        {
            var targetChanged = viewModel.Update(intervention);
            if (_hasRendered && IsVisible && targetBoundsChanged)
            {
                if (!PositionOverTargetWindow())
                {
                    await viewModel.AcknowledgeCoverageFailureAsync("TargetBoundsUpdateFailed");
                }
            }
            if (targetChanged && _hasRendered && IsVisible)
            {
                if (PositionOverTargetWindow())
                {
                    await CompleteVisibleHandshakeAsync(viewModel);
                }
                else await viewModel.AcknowledgeCoverageFailureAsync("TargetBoundsCoverageFailed");
            }
        }
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _hasRendered = true;
        if (IsVisible)
        {
            if (DataContext is ApplicationInterventionOverlayViewModel viewModel)
            {
                if (PositionOverTargetWindow())
                {
                    await CompleteVisibleHandshakeAsync(viewModel);
                }
                else await viewModel.AcknowledgeCoverageFailureAsync("TargetBoundsCoverageFailed");
            }
        }
    }

    private async Task CompleteVisibleHandshakeAsync(ApplicationInterventionOverlayViewModel viewModel)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsVisible || Opacity < 1 || handle == IntPtr.Zero)
        {
            await viewModel.AcknowledgeCoverageFailureAsync("OverlayNotVisible");
            return;
        }

        // Tell the Agent which Desktop window will cover the target before activating it.
        // That lets the Agent validate the blocked process while it is still foreground.
        var acknowledged = await viewModel.AcknowledgeVisibleAsync(handle.ToInt64());
        if (!acknowledged) return;

        if (!IsActive) Activate();
        if (!IsActive || GetForegroundWindow() != handle)
        {
            await viewModel.AcknowledgeCoverageFailureAsync("OverlayCouldNotReceiveKeyboardFocus");
            return;
        }

        if (!RequestExceptionButton.Focus()) Keyboard.Focus(this);
    }

    private bool PositionOverTargetWindow()
    {
        var bounds = _targetBounds;
        if (!IsVisible || bounds is null || bounds.Width <= 0 || bounds.Height <= 0
            || bounds.Width > 65536 || bounds.Height > 65536
            || bounds.X is < -65536 or > 65536 || bounds.Y is < -65536 or > 65536) return false;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;

        var success = SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SwpNoZOrder | SwpNoActivate);
        if (!success || !GetWindowRect(handle, out var overlayRect)) return false;
        var coversTarget = overlayRect.Left == bounds.X && overlayRect.Top == bounds.Y
            && overlayRect.Right - overlayRect.Left == bounds.Width
            && overlayRect.Bottom - overlayRect.Top == bounds.Height;
        if (coversTarget) Opacity = 1;
        return coversTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
