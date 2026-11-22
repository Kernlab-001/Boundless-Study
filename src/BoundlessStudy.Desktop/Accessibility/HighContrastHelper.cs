using System;
using System.ComponentModel;
using System.Windows;

namespace BoundlessStudy.Desktop.Accessibility;

/// <summary>
/// Monitors and exposes Windows High Contrast mode state and transitions.
/// Allows WPF views, view models, and controls to react to theme and accessibility contrast changes.
/// </summary>
public static class HighContrastHelper
{
    private static bool _isHighContrast;

    static HighContrastHelper()
    {
        _isHighContrast = SystemParameters.HighContrast;
        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
    }

    /// <summary>
    /// Gets a value indicating whether Windows High Contrast mode is currently active.
    /// </summary>
    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// Occurs when Windows High Contrast mode is enabled or disabled.
    /// </summary>
    public static event EventHandler<bool>? HighContrastChanged;

    private static void OnStaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            var current = SystemParameters.HighContrast;
            if (current != _isHighContrast)
            {
                _isHighContrast = current;
                HighContrastChanged?.Invoke(null, current);
            }
        }
    }

    #region Attached Property: HighContrastBorderThickness

    /// <summary>
    /// Attached property to specify an alternative border thickness when Windows High Contrast mode is enabled.
    /// </summary>
    public static readonly DependencyProperty HighContrastBorderThicknessProperty =
        DependencyProperty.RegisterAttached(
            "HighContrastBorderThickness",
            typeof(Thickness?),
            typeof(HighContrastHelper),
            new FrameworkPropertyMetadata(null));

    public static Thickness? GetHighContrastBorderThickness(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (Thickness?)obj.GetValue(HighContrastBorderThicknessProperty);
    }

    public static void SetHighContrastBorderThickness(DependencyObject obj, Thickness? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(HighContrastBorderThicknessProperty, value);
    }

    #endregion
}
