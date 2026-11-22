// Accessibility contract tests for the WPF desktop XAML.
//
// Source of truth for the manual side: docs/05-quality/windows11-accessibility-verification.md.
// Requirements covered statically: BS-NFR-A11Y-001 (keyboard operation), BS-NFR-A11Y-002
// (UIA names), BS-NFR-A11Y-004 (high contrast), BS-NFR-A11Y-009 (error/recovery content),
// and the A3 release condition that Windows 11 accessibility coverage is verified.
//
// These tests are a static markup contract, NOT accessibility verification. They parse
// XAML with XDocument and inspect attributes. They do not instantiate WPF controls, run
// automation peers, resolve bindings or styles, traverse focus, launch a screen reader,
// enable a high-contrast theme, or change DPI. Passing tests only mean the markup follows
// the rules below. Every manual check in the verification document remains NOT EXECUTED
// until a person performs it on Windows 11 and records evidence.
//
// Rules:
//   1. Every .xaml file under src\BoundlessStudy.Desktop (excluding bin/obj) is parsed.
//   2. Every interactive control (Button, TextBox, ComboBox, CheckBox, RadioButton,
//      ListBox, ListView, MenuItem, Hyperlink, RepeatButton, ToggleButton, PasswordBox,
//      RichTextBox, Slider, ProgressBar, TabItem, DatePicker, TreeViewItem, DataGrid) has a
//      non-empty accessible name from AutomationProperties.Name or
//      AutomationProperties.LabeledBy. Content controls may instead use visible
//      Content/Header/Text (including AccessText/Run/TextBlock text). TextBox,
//      ComboBox, PasswordBox, Slider, ListBox and ListView have no text fallback because their content is
//      data, not a label, so they need Name or LabeledBy.
//   3. Every Window/UserControl root has AutomationProperties.Name or a non-empty Title
//      (Title applies to Window only; a UserControl needs AutomationProperties.Name).
//   4. Tab order is deterministic: either every tabbable interactive control in a file
//      has a non-negative TabIndex, or none does and document order is the intended order.
//      A partial set of TabIndex values is rejected because controls without one default
//      to 0 and can jump ahead of explicitly ordered controls. Primary recovery actions
//      (Resume/Discard) must not be removed from tab order with IsTabStop=False or
//      Focusable=False. This is a static proxy only; actual traversal is a manual check.
//   5. Color-bearing properties may not contain literal hex colors other than opaque
//      black (#000000) or opaque white (#FFFFFF). SystemColors, DynamicResource,
//      StaticResource and a theme palette are the supported mechanism so Windows
//      high-contrast themes can replace colors. Literal colors in Style/Trigger setters
//      are checked too. Named color literals (for example Gray, Red, Blue) are rejected.
//   6. References to SystemColors and High Contrast system brushes MUST use DynamicResource
//      so that runtime contrast theme switching on Windows 11 takes effect without restarting.
//   7. Focus visualization must not be suppressed: FocusVisualStyle="{x:Null}" is forbidden.
//   8. If a recovery action labelled Resume or Discard exists, its visible content must
//      contain a WPF access-key marker (an underscore before a letter or digit, for
//      example Content="_Resume" or <AccessText Text="_Resume"/>). Two recovery actions
//      must not share the same access key. If no recovery action exists, the rule is
//      vacuously satisfied; it becomes active automatically when the XAML is added.
//   9. All access keys across any single view must be unique (no duplicate mnemonics).
//  10. At most one button with IsDefault="True" and at most one with IsCancel="True" per view.
//  11. The authoritative manual verification runbook (docs/05-quality/windows11-accessibility-verification.md)
//      must exist on disk and contain all required verification sections.
//
// Discovery is data-driven: a new .xaml file is included automatically, and no screen
// name, file name or fixed control count is hard-coded. Template content is included;
// a template is still XAML that can expose unnamed controls, and the stricter result is
// acceptable for this small desktop surface.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BoundlessStudy.Architecture.Tests;

public class AccessibilityContractTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly string DesktopProjectDirectory =
        Path.Combine(RepoRoot, "src", "BoundlessStudy.Desktop");

    private static readonly string ManualChecklist =
        "docs/05-quality/windows11-accessibility-verification.md";

    /// <summary>Controls a keyboard or assistive-technology user can operate.</summary>
    private static readonly HashSet<string> InteractiveControlNames = new(StringComparer.Ordinal)
    {
        "Button",
        "TextBox",
        "ComboBox",
        "CheckBox",
        "RadioButton",
        "ListBox",
        "ListView",
        "MenuItem",
        "Hyperlink",
        "RepeatButton",
        "ToggleButton",
        "PasswordBox",
        "RichTextBox",
        "Slider",
        "ProgressBar",
        "TabItem",
        "DatePicker",
        "TreeViewItem",
        "DataGrid",
    };

    /// <summary>
    /// Controls whose visible content is a label and may supply an accessible name when
    /// AutomationProperties.Name/LabeledBy is absent.
    /// </summary>
    private static readonly HashSet<string> TextContentControlNames = new(StringComparer.Ordinal)
    {
        "Button",
        "CheckBox",
        "RadioButton",
        "MenuItem",
        "Hyperlink",
        "RepeatButton",
        "ToggleButton",
        "TabItem",
    };

    /// <summary>Controls that can be the primary recovery action.</summary>
    private static readonly HashSet<string> RecoveryActionControlNames = new(StringComparer.Ordinal)
    {
        "Button",
        "MenuItem",
        "Hyperlink",
        "RepeatButton",
        "ToggleButton",
    };

    /// <summary>Properties that can carry a text/background/border/icon color.</summary>
    private static readonly HashSet<string> ColorPropertyNames = new(StringComparer.Ordinal)
    {
        "Background",
        "BorderBrush",
        "CaretBrush",
        "Color",
        "Fill",
        "Foreground",
        "OpacityMask",
        "SelectionBrush",
        "SelectionTextBrush",
        "Stroke",
    };

    /// <summary>
    /// The only literal hex colors accepted by the static rule: opaque black and opaque
    /// white. Everything else must come from SystemColors or a named resource so the
    /// Windows high-contrast theme can substitute it.
    /// </summary>
    private static readonly HashSet<string> AllowedLiteralArgbColors = new(StringComparer.Ordinal)
    {
        "FF000000",
        "FFFFFFFF",
    };

    /// <summary>
    /// Known non-themeable named colors that bypass High Contrast mode and are strictly
    /// forbidden on color-bearing properties.
    /// </summary>
    private static readonly HashSet<string> ForbiddenNamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Red", "Green", "Blue", "Yellow", "Orange", "Purple", "Pink", "Brown",
        "Cyan", "Magenta", "Gray", "LightGray", "DarkGray", "SlateGray",
        "LightBlue", "DarkBlue", "LightGreen", "DarkGreen", "Navy", "Teal",
        "Maroon", "Olive", "Lime", "Aqua", "Fuchsia", "Silver", "Gold",
        "Beige", "Ivory", "Khaki", "Lavender", "Crimson", "Coral", "Salmon",
    };

    /// <summary>
    /// Placeholder names that do not qualify as meaningful accessible names.
    /// </summary>
    private static readonly HashSet<string> MeaninglessPlaceholderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "btn", "textbox", "txt", "combobox", "click", "click here",
        "todo", "test", "temp", "placeholder", "unnamed", "control", "item",
        "label", "text", "...", "---", "xxx",
    };

    public static IEnumerable<object[]> DesktopXamlFiles()
    {
        foreach (var file in DiscoverDesktopXamlFiles())
        {
            yield return new object[] { ToRelativePath(file) };
        }
    }

    [Fact]
    public void DesktopXamlFilesExist()
    {
        Assert.True(
            Directory.Exists(DesktopProjectDirectory),
            $"Desktop project directory not found: {DesktopProjectDirectory}");

        Assert.True(
            DiscoverDesktopXamlFiles().Count > 0,
            "No .xaml files were found under src/BoundlessStudy.Desktop (excluding bin/obj). " +
            "The accessibility contract must not silently pass when there is no UI to inspect.");
    }

    [Fact]
    public void ManualAccessibilityVerificationChecklistExistsAndContainsAllRequiredSections()
    {
        var checklistFullPath = Path.Combine(RepoRoot, ManualChecklist);
        Assert.True(
            File.Exists(checklistFullPath),
            $"Authoritative manual accessibility verification runbook not found at: {ManualChecklist}. " +
            "A release check must not be marked complete without the documented Windows 11 runbook.");

        var content = File.ReadAllText(checklistFullPath);
        Assert.True(content.Length > 500, $"{ManualChecklist} appears empty or truncated.");

        // Assert presence of mandatory sections required for Windows 11 accessibility signoff
        Assert.Contains("Manual high-contrast checklist", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Narrator", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Magnifier", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("On-Screen Keyboard", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aquatic", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Desert", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dusk", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Night Sky", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("narrator.exe", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("magnify.exe", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("osk.exe", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BS-NFR-A11Y-001", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BS-NFR-A11Y-002", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void WindowAndUserControlRootsExposeAnAccessibleName(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var root = document.Root;
        if (root is null)
        {
            Assert.Fail($"{relativePath} has no root element.");
            return;
        }

        if (root.Name.LocalName is not ("Window" or "UserControl"))
        {
            // Application and ResourceDictionary roots are not windows or user controls.
            return;
        }

        var hasName = HasMeaningfulAttribute(root, "AutomationProperties.Name");
        var hasTitle = root.Name.LocalName == "Window" && HasMeaningfulAttribute(root, "Title");

        Assert.True(
            hasName || hasTitle,
            $"{relativePath}:{LineNumber(root)} <{root.Name.LocalName}> has no accessible root name. " +
            "Set AutomationProperties.Name, or a non-empty Title on a Window, so the window is identifiable " +
            "in the screen reader's window list. A UI Automation Title is not a substitute for a UserControl.");
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void InteractiveControlsExposeAccessibleNames(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        foreach (var control in document.Descendants().Where(IsInteractiveControl))
        {
            if (GetAccessibleNameSource(control) is not null)
            {
                continue;
            }

            failures.Add(
                $"{relativePath}:{LineNumber(control)} <{control.Name.LocalName}> has no accessible name. " +
                "Set AutomationProperties.Name, or AutomationProperties.LabeledBy to a visible label. " +
                "Content controls may instead use visible Content/Header/Text (including AccessText or Run). " +
                "TextBox, PasswordBox, ComboBox, Slider, ListBox and ListView have no text fallback because their text is data, " +
                "not a label; use AutomationProperties.Name or LabeledBy.");
        }

        Assert.True(
            failures.Count == 0,
            "Accessible-name contract failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void InteractiveControlsDoNotUsePlaceholderOrMeaninglessNames(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        foreach (var control in document.Descendants().Where(IsInteractiveControl))
        {
            var rawName = AttributeValue(control, "AutomationProperties.Name");
            if (rawName is null)
            {
                continue;
            }

            var trimmed = rawName.Trim();
            if (MeaninglessPlaceholderNames.Contains(trimmed))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} <{control.Name.LocalName}> has placeholder accessible name " +
                    $"'{trimmed}'. Use a descriptive accessible name explaining the action or purpose to assistive technology.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Placeholder accessible name failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void AutomationPropertiesLabeledByTargetsMustResolveInSameDocument(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        // Collect all element names declared in this XAML document
        var declaredNames = new HashSet<string>(
            document.Descendants()
                .SelectMany(e => new[] { AttributeValue(e, "Name"), AttributeValue(e, "x:Name") })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())!,
            StringComparer.Ordinal);

        foreach (var control in document.Descendants().Where(IsInteractiveControl))
        {
            var labeledBy = AttributeValue(control, "AutomationProperties.LabeledBy");
            if (string.IsNullOrWhiteSpace(labeledBy))
            {
                continue;
            }

            var targetName = ExtractElementNameReference(labeledBy);
            if (targetName is not null && !declaredNames.Contains(targetName))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} <{control.Name.LocalName}> AutomationProperties.LabeledBy " +
                    $"references '{targetName}', but no element with Name/x:Name='{targetName}' exists in this document. " +
                    "Broken label references result in unnamed controls at runtime.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "AutomationProperties.LabeledBy resolution failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void TabOrderIsDeterministicAndPrimaryActionsAreReachable(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var controls = document.Descendants().Where(IsInteractiveControl).ToArray();
        var tabbable = controls.Where(IsTabbable).ToArray();
        var withTabIndex = tabbable.Where(control => GetTabIndex(control) is not null).ToArray();
        var failures = new List<string>();

        // Rule: use document order (no TabIndex at all) or assign TabIndex to every
        // tabbable control. Mixing the two makes default-0 controls jump ahead of
        // explicit positions, which is the usual cause of an illogical traversal.
        if (withTabIndex.Length > 0)
        {
            foreach (var control in tabbable.Where(control => GetTabIndex(control) is null))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} <{control.Name.LocalName}> has no TabIndex while " +
                    "another tabbable control in this XAML file does. Assign TabIndex to every tabbable " +
                    "interactive control, or remove it from all of them and rely on document order.");
            }
        }

        foreach (var control in withTabIndex)
        {
            var raw = GetTabIndex(control);
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tabIndex)
                || tabIndex < 0)
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} <{control.Name.LocalName}> has invalid TabIndex " +
                    $"'{raw}'. Use a non-negative integer, or remove all TabIndex values and rely on document order.");
            }
        }

        // Primary recovery actions must not be removed from the keyboard path. Static
        // analysis cannot prove the focus sequence, so the manual keyboard pass checks
        // the actual Tab/Shift+Tab order and focus visibility.
        foreach (var control in controls.Where(IsRecoveryAction))
        {
            if (!IsTabbable(control))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} recovery action <{control.Name.LocalName}> is marked " +
                    "IsTabStop=\"False\" or Focusable=\"False\". Primary recovery actions must remain in the " +
                    "keyboard tab order.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Tab-order contract failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void FocusVisualStyleMustNotBeDisabledOrNull(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        foreach (var element in document.Descendants())
        {
            var directFocusVisual = AttributeValue(element, "FocusVisualStyle");
            if (directFocusVisual is not null && IsNullOrEmptyBinding(directFocusVisual))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(element)} <{element.Name.LocalName}> sets FocusVisualStyle=\"{directFocusVisual}\". " +
                    "Suppressing the focus visual indicator makes keyboard navigation invisible, violating BS-NFR-A11Y-001 and WCAG 2.4.7.");
            }

            if (element.Name.LocalName == "Setter")
            {
                var property = AttributeValue(element, "Property");
                var value = AttributeValue(element, "Value");
                if (property is not null && LastPropertyName(property) == "FocusVisualStyle"
                    && (value is null || IsNullOrEmptyBinding(value)))
                {
                    failures.Add(
                        $"{relativePath}:{LineNumber(element)} Setter Property=\"{property}\" sets Value=\"{value}\". " +
                        "FocusVisualStyle must not be suppressed via styles.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Focus visual style contract failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void TextAndBackgroundColorsUseThemeResources(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                // Style and trigger values are handled below, where the Property
                // attribute tells us whether Value is a color at all.
                if (element.Name.LocalName is "Setter" or "Trigger"
                    && attribute.Name.LocalName.Equals("Value", StringComparison.Ordinal))
                {
                    continue;
                }

                var propertyName = LastPropertyName(attribute.Name.LocalName);
                if (ColorPropertyNames.Contains(propertyName))
                {
                    CheckLiteralColor(element, attribute.Name.LocalName, attribute.Value, attribute, failures);
                }
            }
        }

        foreach (var element in document.Descendants().Where(
                     element => element.Name.LocalName is "Setter" or "Trigger"))
        {
            var property = AttributeValue(element, "Property");
            var value = AttributeValue(element, "Value");
            if (property is null || value is null)
            {
                continue;
            }

            if (ColorPropertyNames.Contains(LastPropertyName(property)))
            {
                CheckLiteralColor(element, "Value", value, element, failures);
            }
        }

        // A Color element can carry its value as element text rather than an attribute.
        foreach (var element in document.Descendants().Where(
                     element => element.Name.LocalName == "Color" && !element.HasElements))
        {
            CheckLiteralColor(element, "Color", element.Value, element, failures);
        }

        Assert.True(
            failures.Count == 0,
            $"Hard-coded color contract failures (manual checklist: {ManualChecklist}, " +
            "section 'Manual high-contrast checklist'):" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void ColorPropertiesMustNotUseHardcodedNamedColors(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (element.Name.LocalName is "Setter" or "Trigger"
                    && attribute.Name.LocalName.Equals("Value", StringComparison.Ordinal))
                {
                    continue;
                }

                var propertyName = LastPropertyName(attribute.Name.LocalName);
                if (ColorPropertyNames.Contains(propertyName)
                    && ForbiddenNamedColors.Contains(attribute.Value.Trim()))
                {
                    failures.Add(
                        $"{relativePath}:{LineNumber(attribute)} <{element.Name.LocalName}> {attribute.Name.LocalName}=\"{attribute.Value.Trim()}\" " +
                        "uses a hard-coded named color. Hardcoded colors bypass Windows 11 High Contrast themes. " +
                        "Use a SystemColors brush or DynamicResource instead.");
                }
            }
        }

        foreach (var element in document.Descendants().Where(
                     element => element.Name.LocalName is "Setter" or "Trigger"))
        {
            var property = AttributeValue(element, "Property");
            var value = AttributeValue(element, "Value");
            if (property is not null && value is not null
                && ColorPropertyNames.Contains(LastPropertyName(property))
                && ForbiddenNamedColors.Contains(value.Trim()))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(element)} Setter Property=\"{property}\" Value=\"{value.Trim()}\" " +
                    "uses a hard-coded named color. Use DynamicResource with SystemColors instead.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Forbidden named color failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void SystemColorsAndThemeBrushesMustUseDynamicResource(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        // System colors must be referenced with DynamicResource so runtime theme changes in Windows 11
        // (e.g. toggling High Contrast Desert or Aquatic) adapt immediately without restarting the application.
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var val = attribute.Value.Trim();
                if (val.StartsWith("{StaticResource", StringComparison.OrdinalIgnoreCase)
                    && (val.Contains("SystemColors.", StringComparison.OrdinalIgnoreCase)
                        || val.Contains("SystemParameters.", StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add(
                        $"{relativePath}:{LineNumber(attribute)} <{element.Name.LocalName}> {attribute.Name.LocalName}=\"{val}\" " +
                        "uses StaticResource for a system color or parameter. SystemColors MUST use DynamicResource " +
                        "so Windows 11 Contrast Theme switches update dynamically at runtime (BS-NFR-A11Y-004).");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "StaticResource SystemColor failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void PrimaryRecoveryActionsExposeAccessKeys(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var recoveryActions = document.Descendants().Where(IsRecoveryAction).ToArray();
        var failures = new List<string>();
        var keys = new Dictionary<char, XElement>();

        foreach (var control in recoveryActions)
        {
            var content = GetContentText(control);
            var key = FindAccessKey(content);

            if (key is null)
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} recovery action <{control.Name.LocalName}> is " +
                    $"labelled '{content}' but has no WPF access key. Put an underscore before a letter or " +
                    "digit in the visible Content, for example Content=\"_Resume\" or " +
                    "<AccessText Text=\"_Discard\"/>. AutomationProperties.Name alone does not create an " +
                    "access key.");
                continue;
            }

            var normalizedKey = char.ToUpperInvariant(key.Value);
            if (keys.TryGetValue(normalizedKey, out var existing))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(control)} recovery action <{control.Name.LocalName}> uses " +
                    $"access key '{normalizedKey}', which is already used by another recovery action at line " +
                    $"{LineNumber(existing)}. Primary recovery actions need distinct access keys.");
            }
            else
            {
                keys[normalizedKey] = control;
            }
        }

        Assert.True(
            failures.Count == 0,
            "Recovery access-key contract failures:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void AccessKeysDoNotCollideWithinView(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();
        var seenKeys = new Dictionary<char, XElement>();

        foreach (var element in document.Descendants().Where(IsInteractiveControl))
        {
            var content = GetContentText(element);
            var key = FindAccessKey(content);
            if (key is null)
            {
                continue;
            }

            var normalized = char.ToUpperInvariant(key.Value);
            if (seenKeys.TryGetValue(normalized, out var existing))
            {
                failures.Add(
                    $"{relativePath}:{LineNumber(element)} <{element.Name.LocalName}> access key '{normalized}' " +
                    $"collides with <{existing.Name.LocalName}> at line {LineNumber(existing)}. " +
                    "All access keys within a view must be distinct to prevent ambiguous keyboard invocation.");
            }
            else
            {
                seenKeys[normalized] = element;
            }
        }

        Assert.True(
            failures.Count == 0,
            "Access key collision failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(DesktopXamlFiles))]
    public void WindowDialogsMustNotHaveMultipleDefaultOrCancelButtons(string relativePath)
    {
        var document = LoadXaml(relativePath);
        var failures = new List<string>();

        var defaultButtons = document.Descendants()
            .Where(e => e.Name.LocalName is "Button" or "RepeatButton"
                        && GetBooleanAttribute(e, "IsDefault") == true)
            .ToArray();

        if (defaultButtons.Length > 1)
        {
            failures.Add(
                $"{relativePath} declares {defaultButtons.Length} buttons with IsDefault=\"True\". " +
                "Only one button per view may be the default Enter key action.");
        }

        var cancelButtons = document.Descendants()
            .Where(e => e.Name.LocalName is "Button" or "RepeatButton"
                        && GetBooleanAttribute(e, "IsCancel") == true)
            .ToArray();

        if (cancelButtons.Length > 1)
        {
            failures.Add(
                $"{relativePath} declares {cancelButtons.Length} buttons with IsCancel=\"True\". " +
                "Only one button per view may be the cancel Escape key action.");
        }

        Assert.True(
            failures.Count == 0,
            "Default/Cancel button contract failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BoundlessStudy.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "BoundlessStudy.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the Boundless Study solution file above the test directory.");
    }

    private static IReadOnlyList<string> DiscoverDesktopXamlFiles()
    {
        if (!Directory.Exists(DesktopProjectDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(DesktopProjectDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string path)
    {
        var relativePath = Path.GetRelativePath(DesktopProjectDirectory, path);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToRelativePath(string absolutePath)
        => Path.GetRelativePath(RepoRoot, absolutePath).Replace('\\', '/');

    private static XDocument LoadXaml(string relativePath)
    {
        var fullPath = Path.Combine(
            RepoRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            // Share read/write so a concurrent editor or build cannot make the contract
            // test fail with a sharing violation. This is a read-only open.
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return XDocument.Load(stream, LoadOptions.SetLineInfo);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Could not read {relativePath} while checking the accessibility contract.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"Access denied reading {relativePath} while checking the accessibility contract.", exception);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException(
                $"{relativePath} is not well-formed XML/XAML, so accessibility rules cannot be checked. " +
                "Fix the markup first.", exception);
        }
    }

    private static bool IsInteractiveControl(XElement element)
        => InteractiveControlNames.Contains(element.Name.LocalName);

    private static bool IsRecoveryAction(XElement control)
    {
        if (!RecoveryActionControlNames.Contains(control.Name.LocalName))
        {
            return false;
        }

        var label = GetControlLabelText(control);
        return ContainsWord(label, "Resume") || ContainsWord(label, "Discard");
    }

    private static string? GetAccessibleNameSource(XElement control)
    {
        if (HasMeaningfulAttribute(control, "AutomationProperties.Name"))
        {
            return "AutomationProperties.Name";
        }

        if (HasMeaningfulAttribute(control, "AutomationProperties.LabeledBy"))
        {
            // The target is resolved by WPF at runtime. Static analysis can only confirm
            // that a label reference exists; the manual UIA check confirms the spoken name.
            return "AutomationProperties.LabeledBy";
        }

        if (TextContentControlNames.Contains(control.Name.LocalName)
            && !string.IsNullOrWhiteSpace(GetContentText(control)))
        {
            return "visible content";
        }

        return null;
    }

    private static string GetControlLabelText(XElement control)
    {
        var parts = new List<string>();
        AddMeaningfulText(parts, AttributeValue(control, "AutomationProperties.Name"));
        AddMeaningfulText(parts, GetContentText(control));
        return string.Join(" ", parts);
    }

    private static string GetContentText(XElement control)
    {
        var parts = new List<string>();
        AddMeaningfulText(parts, AttributeValue(control, "Content"));
        AddMeaningfulText(parts, AttributeValue(control, "Header"));

        // Direct text, for example <Button>Save</Button>.
        foreach (var node in control.DescendantNodes())
        {
            if (node is XText text)
            {
                AddMeaningfulText(parts, text.Value);
            }
        }

        // Text held in an attribute, for example <TextBlock Text="Save"/> or
        // <AccessText Text="_Resume"/>. XElement.Value does not include attributes.
        foreach (var descendant in control.Descendants())
        {
            if (descendant.Name.LocalName is "TextBlock" or "AccessText" or "Run" or "Label")
            {
                AddMeaningfulText(parts, AttributeValue(descendant, "Text"));
            }
        }

        return string.Join(" ", parts);
    }

    private static void AddMeaningfulText(ICollection<string> parts, string? value)
    {
        if (IsMeaningfulText(value))
        {
            parts.Add(value!.Trim());
        }
    }

    /// <summary>
    /// A label needs at least one letter or digit. Whitespace, a bare underscore, a
    /// markup null, or a symbol-only glyph is not an accessible name and must be
    /// replaced with AutomationProperties.Name.
    /// </summary>
    private static bool IsMeaningfulText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("{x:Null}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Any(char.IsLetterOrDigit);
    }

    private static bool IsNullOrEmptyBinding(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Equals("{x:Null}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("x:Null", StringComparison.OrdinalIgnoreCase)
               || string.IsNullOrWhiteSpace(trimmed);
    }

    private static string? ExtractElementNameReference(string bindingExpression)
    {
        var match = Regex.Match(bindingExpression, @"ElementName\s*=\s*([a-zA-Z0-9_]+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var refMatch = Regex.Match(bindingExpression, @"\{x:Reference\s+([a-zA-Z0-9_]+)\}");
        if (refMatch.Success)
        {
            return refMatch.Groups[1].Value;
        }

        return null;
    }

    private static char? FindAccessKey(string content)
    {
        for (var index = 0; index < content.Length - 1; index++)
        {
            if (content[index] != '_')
            {
                continue;
            }

            // A doubled underscore is an escaped literal underscore, not a mnemonic.
            if (index > 0 && content[index - 1] == '_')
            {
                continue;
            }

            var candidate = content[index + 1];
            if (candidate == '_' || !char.IsLetterOrDigit(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool ContainsWord(string text, string word)
    {
        var start = 0;
        while (start <= text.Length - word.Length)
        {
            var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var beforeIsBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterIsBoundary = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            if (beforeIsBoundary && afterIsBoundary)
            {
                return true;
            }

            start = afterIndex;
        }

        return false;
    }

    private static string? GetTabIndex(XElement control)
        => AttributeValue(control, "TabIndex")
           ?? AttributeValue(control, "KeyboardNavigation.TabIndex");

    private static bool IsTabbable(XElement control)
        => GetBooleanAttribute(control, "IsTabStop") != false
           && GetBooleanAttribute(control, "KeyboardNavigation.IsTabStop") != false
           && GetBooleanAttribute(control, "Focusable") != false;

    private static bool? GetBooleanAttribute(XElement element, string localName)
    {
        var value = AttributeValue(element, localName);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool HasMeaningfulAttribute(XElement element, string localName)
        => IsMeaningfulText(AttributeValue(element, localName));

    private static string? AttributeValue(XElement element, string localName)
        => element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(localName, StringComparison.Ordinal))
            ?.Value;

    private static string LastPropertyName(string propertyName)
    {
        var separator = propertyName.LastIndexOf('.');
        return separator >= 0 ? propertyName[(separator + 1)..] : propertyName;
    }

    private static void CheckLiteralColor(
        XElement element,
        string propertyName,
        string value,
        XObject location,
        ICollection<string> failures)
    {
        var argb = NormalizeArgb(value);
        if (argb is null || AllowedLiteralArgbColors.Contains(argb))
        {
            return;
        }

        failures.Add(
            $"{ToDisplayLocation(location)} <{element.Name.LocalName}> {propertyName}=\"{value.Trim()}\" " +
            "is a hard-coded color outside the literal allowlist (only opaque #000000 and #FFFFFF are " +
            "accepted). Use a SystemColors brush or a named DynamicResource/StaticResource so the Windows " +
            $"high-contrast theme can replace it. Then complete the manual high-contrast checklist in " +
            $"{ManualChecklist}, section 'Manual high-contrast checklist'.");
    }

    private static string ToDisplayLocation(XObject node)
    {
        var line = LineNumber(node);
        return line > 0 ? $"line {line}" : "unknown line";
    }

    /// <summary>
    /// Normalizes #RGB, #ARGB, #RRGGBB and #AARRGGBB to an eight-digit AARRGGBB string.
    /// Returns null for anything that is not a complete literal hex color.
    /// </summary>
    private static string? NormalizeArgb(string value)
    {
        var literal = value.Trim();
        if (literal.Length < 4 || literal[0] != '#')
        {
            return null;
        }

        var digits = literal[1..];
        if (digits.Length is not (3 or 4 or 6 or 8))
        {
            return null;
        }

        if (!digits.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        var argb = digits.Length switch
        {
            3 => "FF" + ExpandHexDigits(digits),
            4 => ExpandHexDigits(digits),
            6 => "FF" + digits,
            8 => digits,
            _ => null,
        };

        return argb?.ToUpperInvariant();
    }

    private static string ExpandHexDigits(string digits)
        => string.Concat(digits.Select(character => new string(character, 2)));

    private static int LineNumber(XObject node)
        => node is IXmlLineInfo { } lineInfo && lineInfo.HasLineInfo()
            ? lineInfo.LineNumber
            : 0;
}
