using System.Xml.Linq;

namespace BoundlessStudy.Architecture.Tests;

/// <summary>Static guardrails for the Agent-driven Desktop overlay. Live Windows verification remains required.</summary>
public sealed class ApplicationInterventionOverlayTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string DesktopRoot = Path.Combine(RepositoryRoot, "src", "BoundlessStudy.Desktop");

    [Fact]
    public void Overlay_IsInitiallyNonactivatingAndAccessible()
    {
        var document = XDocument.Load(Path.Combine(DesktopRoot, "Views", "ApplicationInterventionOverlay.xaml"));
        var root = document.Root!;
        Assert.Equal("True", (string?)root.Attribute("Topmost"));
        Assert.Equal("False", (string?)root.Attribute("ShowActivated"));
        Assert.Equal("0", (string?)root.Attribute("Opacity"));
        Assert.Equal("None", (string?)root.Attribute("WindowStyle"));
        Assert.False(string.IsNullOrWhiteSpace((string?)root.Attribute("AutomationProperties.Name")));

        var interactive = root.Descendants().Where(e => e.Name.LocalName is "Button" or "ComboBox").ToArray();
        Assert.NotEmpty(interactive);
        Assert.All(interactive, control => Assert.False(string.IsNullOrWhiteSpace(
            (string?)control.Attribute("AutomationProperties.Name"))));
        Assert.Contains(interactive, e => e.Name.LocalName == "Button"
            && ((string?)e.Attribute("Command"))?.Contains("RequestExceptionCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Overlay_CoversTargetBoundsBeforeAcknowledgingAndCapturesKeyboardAfterward()
    {
        var code = File.ReadAllText(Path.Combine(DesktopRoot, "Views", "ApplicationInterventionOverlay.xaml.cs"));
        var visibleCheck = code.IndexOf("if (IsVisible)", StringComparison.Ordinal);
        var ackCall = code.IndexOf("AcknowledgeVisibleAsync(handle.ToInt64())", StringComparison.Ordinal);

        Assert.True(visibleCheck >= 0 && ackCall > visibleCheck,
            "The Agent must only receive acknowledgement after the visible overlay covers the target.");
        Assert.Contains("SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height", code, StringComparison.Ordinal);
        Assert.Contains("overlayRect.Right - overlayRect.Left == bounds.Width", code, StringComparison.Ordinal);
        Assert.Contains("overlayRect.Bottom - overlayRect.Top == bounds.Height", code, StringComparison.Ordinal);
        Assert.Contains("SwpNoActivate", code, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow() != handle", code, StringComparison.Ordinal);
        Assert.Contains("if (!IsActive) Activate()", code, StringComparison.Ordinal);
        Assert.Contains("ContentRendered", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnlyPollsInterventionsDuringConnectedActiveFocus()
    {
        var code = File.ReadAllText(Path.Combine(DesktopRoot, "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("TimeSpan.FromMilliseconds(300)", code, StringComparison.Ordinal);
        Assert.Contains("ConnectionStatus != AgentConnectionStatus.Connected", code, StringComparison.Ordinal);
        Assert.Contains("CurrentViewModel is not ActiveFocusViewModel", code, StringComparison.Ordinal);
        Assert.Contains("GetApplicationInterventionAsync()", code, StringComparison.Ordinal);
        Assert.Contains("ApplicationInterventionUpdated?.Invoke(null)", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BoundlessStudy.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find BoundlessStudy.slnx from the test output path.");
    }
}
