using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BoundlessStudy.Desktop.Accessibility;

/// <summary>
/// Reusable attached properties and screen-reader announcement helpers for WPF desktop accessibility.
/// Provides Windows 11 UI Automation (UIA) live regions, announcements without stealing focus,
/// default keyboard focus management, and dialog Escape/Enter key behaviors.
/// </summary>
public static class AccessibilityProperties
{
    #region Live Region & Announcement Attached Properties

    /// <summary>
    /// Attached property to set the announcement text for a live region.
    /// Whenever this property changes, a UIA notification event and LiveRegionChanged event
    /// are raised so assistive technologies (such as Narrator) announce the change without stealing keyboard focus.
    /// </summary>
    public static readonly DependencyProperty LiveTextProperty =
        DependencyProperty.RegisterAttached(
            "LiveText",
            typeof(string),
            typeof(AccessibilityProperties),
            new FrameworkPropertyMetadata(null, OnLiveTextChanged));

    public static string? GetLiveText(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (string?)obj.GetValue(LiveTextProperty);
    }

    public static void SetLiveText(DependencyObject obj, string? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(LiveTextProperty, value);
    }

    private static void OnLiveTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.NewValue is string message && !string.IsNullOrWhiteSpace(message))
        {
            // Ensure live setting is at least Polite so screen readers listen to region changes
            if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
            {
                AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
            }

            // Raise notification and live region change event
            ScreenReader.Announce(
                element,
                message,
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent,
                "BoundlessStudyLiveUpdate");
        }
    }

    #endregion

    #region Default Focus Management

    /// <summary>
    /// Attached property to mark a control as the default focused element when its parent window or container loads.
    /// </summary>
    public static readonly DependencyProperty IsDefaultFocusedProperty =
        DependencyProperty.RegisterAttached(
            "IsDefaultFocused",
            typeof(bool),
            typeof(AccessibilityProperties),
            new FrameworkPropertyMetadata(false, OnIsDefaultFocusedChanged));

    public static bool GetIsDefaultFocused(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (bool)obj.GetValue(IsDefaultFocusedProperty);
    }

    public static void SetIsDefaultFocused(DependencyObject obj, bool value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(IsDefaultFocusedProperty, value);
    }

    private static void OnIsDefaultFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || !(bool)e.NewValue)
        {
            return;
        }

        if (element.IsLoaded)
        {
            SetInitialFocus(element);
        }
        else
        {
            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (_, _) =>
            {
                element.Loaded -= loadedHandler;
                SetInitialFocus(element);
            };
            element.Loaded += loadedHandler;
        }
    }

    private static void SetInitialFocus(FrameworkElement element)
    {
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (element.Focusable && element.IsEnabled && element.IsVisible)
            {
                element.Focus();
                Keyboard.Focus(element);
            }
        });
    }

    #endregion

    #region Escape and Enter Key Handling

    /// <summary>
    /// Attached property that allows closing the containing window or dialog when the Escape key is pressed.
    /// </summary>
    public static readonly DependencyProperty EscapeClosesWindowProperty =
        DependencyProperty.RegisterAttached(
            "EscapeClosesWindow",
            typeof(bool),
            typeof(AccessibilityProperties),
            new FrameworkPropertyMetadata(false, OnEscapeClosesWindowChanged));

    public static bool GetEscapeClosesWindow(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (bool)obj.GetValue(EscapeClosesWindowProperty);
    }

    public static void SetEscapeClosesWindow(DependencyObject obj, bool value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(EscapeClosesWindowProperty, value);
    }

    private static void OnEscapeClosesWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewKeyDown += OnPreviewKeyDownForEscape;
        }
        else
        {
            element.PreviewKeyDown -= OnPreviewKeyDownForEscape;
        }
    }

    private static void OnPreviewKeyDownForEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (sender is DependencyObject d)
        {
            var window = Window.GetWindow(d);
            if (window != null)
            {
                e.Handled = true;
                if (ComponentDispatcher.IsThreadModal)
                {
                    try
                    {
                        window.DialogResult = false;
                    }
                    catch (InvalidOperationException)
                    {
                        window.Close();
                    }
                }
                else
                {
                    window.Close();
                }
            }
        }
    }

    /// <summary>
    /// Attached property that binds an <see cref="ICommand"/> to be executed when the Enter key is pressed.
    /// </summary>
    public static readonly DependencyProperty EnterKeyCommandProperty =
        DependencyProperty.RegisterAttached(
            "EnterKeyCommand",
            typeof(ICommand),
            typeof(AccessibilityProperties),
            new FrameworkPropertyMetadata(null, OnEnterKeyCommandChanged));

    public static ICommand? GetEnterKeyCommand(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (ICommand?)obj.GetValue(EnterKeyCommandProperty);
    }

    public static void SetEnterKeyCommand(DependencyObject obj, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(EnterKeyCommandProperty, value);
    }

    private static void OnEnterKeyCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (e.NewValue != null)
        {
            element.KeyDown += OnKeyDownForEnter;
        }
        else
        {
            element.KeyDown -= OnKeyDownForEnter;
        }
    }

    private static void OnKeyDownForEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is DependencyObject d)
        {
            var command = GetEnterKeyCommand(d);
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
    }

    #endregion
}

/// <summary>
/// Helper class for sending non-intrusive UI Automation announcements to screen readers (e.g., Windows Narrator).
/// Uses both modern Windows 10/11 UIA Notification Events and standard LiveRegionChanged events.
/// </summary>
public static class ScreenReader
{
    /// <summary>
    /// Announces a message via UI Automation without moving keyboard focus.
    /// </summary>
    /// <param name="element">The UI element serving as the context or anchor for the announcement.</param>
    /// <param name="announcement">The text to be read by the assistive technology.</param>
    /// <param name="notificationKind">The notification category (e.g. ActionCompleted, ActionAborted, ItemAdded).</param>
    /// <param name="notificationProcessing">Processing priority for the notification.</param>
    /// <param name="activityId">An identifier distinguishing the activity causing the announcement.</param>
    public static void Announce(
        UIElement element,
        string announcement,
        AutomationNotificationKind notificationKind = AutomationNotificationKind.ActionCompleted,
        AutomationNotificationProcessing notificationProcessing = AutomationNotificationProcessing.ImportantMostRecent,
        string activityId = "BoundlessStudyNotification")
    {
        ArgumentNullException.ThrowIfNull(element);

        if (string.IsNullOrWhiteSpace(announcement))
        {
            return;
        }

        // If the element's automation peer exists or can be created, raise UIA notification
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer != null)
        {
            try
            {
                // Modern Windows 10/11 notification event
                peer.RaiseNotificationEvent(notificationKind, notificationProcessing, announcement, activityId);
            }
            catch (Exception)
            {
                // Fall back gracefully if the underlying OS does not support UIA notifications
            }

            try
            {
                // Standard UIA live region change event
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch (Exception)
            {
                // Fall back gracefully
            }
        }
    }
}
