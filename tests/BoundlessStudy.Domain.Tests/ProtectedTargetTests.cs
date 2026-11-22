using System.Collections.Frozen;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Domain.Tests;

/// <summary>
/// Protected blocking targets (A3, approved 2026-11-19 and 2026-11-22). The approved baseline is
/// exactly 24 names and grow-only (8 product components, 10 core Windows processes, and 6 accessibility
/// tools and infrastructure). On 2026-11-22, the user approved adding VoiceAccess.exe, LiveCaptions.exe,
/// and AtBroker.exe, fully resolving the 24-target baseline.
/// </summary>
public class ProtectedTargetTests
{
    /// <summary>Approved grow-only baseline total (A3, 2026-11-19 and 2026-11-22).</summary>
    private const int ApprovedBaselineCount = 24;

    /// <summary>Approved entries fully recovered and verified in the repository.</summary>
    private const int RecoveredBaselineCount = 24;

    /// <summary>Approved entries whose names remain unresolved (0 remaining).</summary>
    private const int UnresolvedBaselineCount = ApprovedBaselineCount - RecoveredBaselineCount;

    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    // ---- Recovered baseline: 8 product components, 10 core Windows processes, 3 accessibility tools. ----

    private static readonly string[] ProductComponentTargets =
    [
        "boundlessstudy.agent.exe",
        "boundlessstudy.agent",
        "boundlessstudy.desktop.exe",
        "boundlessstudy.desktop",
        "boundlessstudy.service.exe",
        "boundlessstudy.service",
        "boundlessstudy.bridge.exe",
        "boundlessstudy.bridge",
    ];

    private static readonly string[] CoreWindowsTargets =
    [
        "csrss.exe",
        "dwm.exe",
        "explorer.exe",
        "lsass.exe",
        "services.exe",
        "smss.exe",
        "svchost.exe",
        "system",
        "wininit.exe",
        "winlogon.exe",
    ];

    private static readonly string[] AccessibilityTargets =
    [
        "narrator.exe",
        "magnify.exe",
        "osk.exe",
        "voiceaccess.exe",
        "livecaptions.exe",
        "atbroker.exe",
    ];

    /// <summary>The 24 approved entries; the grow-only floor that must never shrink.</summary>
    private static readonly string[] RecoveredBaseline =
    [
        .. ProductComponentTargets,
        .. CoreWindowsTargets,
        .. AccessibilityTargets,
    ];

    /// <summary>
    /// Unresolved entries: none. All 24 approved entries are now resolved and verified.
    /// </summary>
    private static readonly string?[] UnresolvedBaselineEntries = Array.Empty<string?>();

    [Fact]
    public void Baseline_structure_records_all_24_approved_entries()
    {
        Assert.Equal(8, ProductComponentTargets.Length);
        Assert.Equal(10, CoreWindowsTargets.Length);
        Assert.Equal(6, AccessibilityTargets.Length);
        Assert.Equal(RecoveredBaselineCount, RecoveredBaseline.Length);
        Assert.Empty(UnresolvedBaselineEntries);
        Assert.Equal(ApprovedBaselineCount, RecoveredBaseline.Length);

        // Verified state: exactly the approved 24 names.
        Assert.True(
            BlockedApp.ExcludedExecutableNames.Count == 24,
            $"BlockedApp.ExcludedExecutableNames has {BlockedApp.ExcludedExecutableNames.Count} entries, " +
            $"expected exactly the {ApprovedBaselineCount} approved baseline entries.");
    }

    [Fact]
    public void Production_set_contains_exactly_the_recovered_baseline()
    {
        var missing = RecoveredBaseline
            .Where(name => !BlockedApp.ExcludedExecutableNames.Contains(name))
            .ToArray();
        Assert.True(
            missing.Length == 0,
            $"Recovered protected targets missing from BlockedApp.ExcludedExecutableNames: {string.Join(", ", missing)}. " +
            "The baseline is grow-only; removing an entry weakens protection.");

        var unexpected = BlockedApp.ExcludedExecutableNames
            .Where(name => !RecoveredBaseline.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            $"Unexpected protected entries: {string.Join(", ", unexpected)}. Only the 21 recovered names are " +
            "verified; add names only from authoritative evidence and update this baseline explicitly.");

        foreach (var name in RecoveredBaseline)
        {
            Assert.Equal(name, name.ToLowerInvariant());
        }
    }

    [Fact]
    public void Protected_set_is_a_frozen_read_only_set_at_runtime()
    {
        Assert.IsAssignableFrom<FrozenSet<string>>(BlockedApp.ExcludedExecutableNames);
        Assert.IsAssignableFrom<IReadOnlySet<string>>(BlockedApp.ExcludedExecutableNames);

        var setView = (ISet<string>)BlockedApp.ExcludedExecutableNames;
        var collectionView = (ICollection<string>)BlockedApp.ExcludedExecutableNames;
        var before = BlockedApp.ExcludedExecutableNames.Count;

        Assert.Throws<NotSupportedException>(() => setView.Add("not-a-protected-target.exe"));
        Assert.Throws<NotSupportedException>(() => setView.Remove("svchost.exe"));
        Assert.Throws<NotSupportedException>(() => setView.Clear());
        Assert.Throws<NotSupportedException>(() => collectionView.Add("not-a-protected-target.exe"));
        Assert.Throws<NotSupportedException>(() => collectionView.Remove("svchost.exe"));
        Assert.Throws<NotSupportedException>(() => collectionView.Clear());

        Assert.Equal(before, BlockedApp.ExcludedExecutableNames.Count);
        Assert.Contains("svchost.exe", (IReadOnlySet<string>)BlockedApp.ExcludedExecutableNames);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\SVCHOST.EXE")]
    [InlineData("\"C:\\Windows\\System32\\svchost.exe\"")]
    [InlineData("C:/Windows/System32/Svchost.Exe")]
    [InlineData(@"\\?\C:\Windows\System32\svchost.exe")]
    [InlineData(@"C:\Program Files\Boundless Study\BoundlessStudy.Agent.exe")]
    [InlineData(@"C:\Program Files\Boundless Study\BoundlessStudy.Bridge")]
    [InlineData(@"C:\Windows\System32\narrator.exe")]
    [InlineData(@"C:\Windows\System32\osk.exe")]
    public void Path_derived_protected_targets_are_detected(string path)
    {
        Assert.True(BlockedApp.IsProtectedTarget(path, null));
    }

    [Theory]
    [InlineData("CSRSS.EXE")]
    [InlineData(" Explorer.exe ")]
    [InlineData("\"magnify.exe\"")]
    [InlineData("BOUNDLESSSTUDY.SERVICE")]
    [InlineData(@"C:\Windows\System32\winlogon.exe")]
    public void Name_only_protected_targets_are_detected_case_insensitively(string name)
    {
        Assert.True(BlockedApp.IsProtectedTarget(null, name));
    }

    [Theory]
    [InlineData("svchost.exe.")]
    [InlineData("svchost.exe ")]
    [InlineData("svchost.exe . . ")]
    [InlineData("\"svchost.exe.\"")]
    [InlineData(@"C:\Windows\System32\svchost.exe.")]
    [InlineData("C:/Windows/System32/SVCHOST.EXE.")]
    [InlineData("C:/Windows/System32/system.")]
    [InlineData("\"C:\\Windows\\System32\\svchost.exe. \"")]
    public void Windows_trailing_dot_and_space_path_forms_are_tolerated(string target)
    {
        Assert.True(
            BlockedApp.IsProtectedTarget(target, null),
            $"'{target}' was not normalized to a protected executable name.");
    }

    [Theory]
    [InlineData("svchost.exe.")]
    [InlineData("\"SVCHOST.EXE.\"")]
    [InlineData("  winlogon.exe.  ")]
    [InlineData("SYSTEM.")]
    [InlineData("boundlessstudy.agent.")]
    public void Windows_trailing_dot_and_space_name_forms_are_tolerated(string name)
    {
        Assert.True(
            BlockedApp.IsProtectedTarget(null, name),
            $"'{name}' was not normalized to a protected executable name.");
    }

    [Theory]
    [InlineData(@"C:\Games\notepad.exe", "notepad.exe")]
    [InlineData(@"C:\Games\notepad.exe.", null)]
    [InlineData(@"C:\Program Files\Google\Chrome\chrome.exe", null)]
    [InlineData("C:/Tools/custom-utility.exe", "custom-utility.exe")]
    [InlineData(null, "code.exe")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("\"\"", null)]
    [InlineData(null, "\"\"")]
    [InlineData(@"C:\Windows\System32\", null)]
    [InlineData(@"C:\Windows\System32", null)]
    public void Unprotected_or_empty_targets_are_not_rejected(string? path, string? name)
    {
        Assert.False(BlockedApp.IsProtectedTarget(path, name));
        BlockedApp.EnsureTargetCanBeBlocked(path, name);
    }

    [Fact]
    public void Every_recovered_entry_is_protected_as_full_path_forward_slash_path_and_bare_name()
    {
        foreach (var name in RecoveredBaseline)
        {
            Assert.True(
                BlockedApp.IsProtectedTarget($@"C:\Program Files\Protected\{name}", null),
                $"'{name}' is not detected from a full Windows path.");
            Assert.True(
                BlockedApp.IsProtectedTarget($"C:/Program Files/Protected/{name}", null),
                $"'{name}' is not detected from a forward-slash path.");
            Assert.True(
                BlockedApp.IsProtectedTarget(null, name),
                $"'{name}' is not detected as a bare executable name.");
        }
    }

    [Fact]
    public void Product_components_are_protected_with_and_without_the_exe_suffix()
    {
        foreach (var stem in new[]
                 {
                     "boundlessstudy.agent",
                     "boundlessstudy.desktop",
                     "boundlessstudy.service",
                     "boundlessstudy.bridge",
                 })
        {
            Assert.Contains(stem, (IReadOnlySet<string>)BlockedApp.ExcludedExecutableNames);
            Assert.Contains(stem + ".exe", (IReadOnlySet<string>)BlockedApp.ExcludedExecutableNames);
            Assert.True(BlockedApp.IsProtectedTarget(null, stem));
            Assert.True(BlockedApp.IsProtectedTarget(null, stem + ".exe"));
        }
    }

    [Theory]
    [InlineData("narrator")]
    [InlineData("magnify")]
    [InlineData("osk")]
    [InlineData("voiceaccess")]
    [InlineData("voiceaccess.exe")]
    [InlineData("livecaptions")]
    [InlineData("livecaptions.exe")]
    [InlineData("atbroker")]
    [InlineData("atbroker.exe")]
    [InlineData("csrss")]
    [InlineData("dwm")]
    [InlineData("explorer")]
    [InlineData("lsass")]
    [InlineData("services")]
    [InlineData("smss")]
    [InlineData("svchost")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("system.exe")]
    public void Core_and_accessibility_targets_are_protected_regardless_of_exe_extension(string target)
    {
        Assert.True(
            BlockedApp.IsProtectedTarget(null, target),
            $"Target '{target}' was not detected as protected without/with extension.");
        Assert.True(
            BlockedApp.IsProtectedTarget($@"C:\Windows\System32\{target}", null),
            $"Path 'C:\\Windows\\System32\\{target}' was not detected as protected.");

        var exception = Assert.Throws<DomainException>(() =>
            BlockedApp.Create(Guid.NewGuid(), "Sneaky", null, target, Now));
        Assert.Equal("BLOCK_TARGET_PROTECTED", exception.Code);
    }

    [Theory]
    [InlineData("VoiceAccess.exe")]
    [InlineData("voiceaccess")]
    [InlineData(@"C:\Windows\System32\VoiceAccess.exe")]
    [InlineData("LiveCaptions.exe")]
    [InlineData("livecaptions")]
    [InlineData(@"C:\Windows\System32\LiveCaptions.exe")]
    [InlineData("AtBroker.exe")]
    [InlineData("atbroker")]
    [InlineData(@"C:\Windows\System32\AtBroker.exe")]
    public void Newly_approved_accessibility_targets_cannot_be_registered_or_blocked_at_runtime(string target)
    {
        bool isPath = target.Contains('\\') || target.Contains('/');
        string? path = isPath ? target : null;
        string? name = isPath ? null : target;

        // 1. IsProtectedTarget detects by name and path
        Assert.True(BlockedApp.IsProtectedTarget(path, name));

        // 2. Rule creation rejects registration
        var createEx = Assert.Throws<DomainException>(() =>
            BlockedApp.Create(Guid.NewGuid(), "Sneaky", path, name, Now));
        Assert.Equal("BLOCK_TARGET_PROTECTED", createEx.Code);

        if (isPath)
        {
            var fileName = Path.GetFileName(target);
            var createWithBoth = Assert.Throws<DomainException>(() =>
                BlockedApp.Create(Guid.NewGuid(), "Sneaky", path, fileName, Now));
            Assert.Equal("BLOCK_TARGET_PROTECTED", createWithBoth.Code);
        }

        // 3. Runtime guard rejects blocking enforcement
        var guardEx = Assert.Throws<DomainException>(() =>
            BlockedApp.EnsureTargetCanBeBlocked(path, name));
        Assert.Equal("BLOCK_TARGET_PROTECTED", guardEx.Code);
    }

    [Fact]
    public void Runtime_guard_rejects_every_recovered_entry_by_path_and_by_name()
    {
        foreach (var name in RecoveredBaseline)
        {
            AssertGuardRejects($@"C:\Windows\System32\{name}", null);
            AssertGuardRejects($"C:/Windows/System32/{name.ToUpperInvariant()}", null);
            AssertGuardRejects(null, name.ToUpperInvariant());
            AssertGuardRejects(@"C:\Program Files\Editor\editor.exe", name);
            AssertGuardRejects($@"C:\Windows\System32\{name}", "editor.exe");
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\winlogon.exe", null)]
    [InlineData("C:/Windows/System32/WINLOGON.EXE", null)]
    [InlineData(@"C:\Windows\System32\winlogon.exe.", null)]
    [InlineData(null, "winlogon.exe")]
    [InlineData(null, "\"WinLogon.Exe\"")]
    [InlineData(@"C:\Program Files\Boundless Study\BoundlessStudy.Agent.exe", "editor.exe")]
    [InlineData(@"C:\Program Files\Editor\editor.exe", "boundlessstudy.agent")]
    public void Rule_creation_rejects_protected_paths_and_names(string? path, string? name)
    {
        var exception = Assert.Throws<DomainException>(() =>
            BlockedApp.Create(Guid.NewGuid(), "Protected", path, name, Now));
        Assert.Equal("BLOCK_TARGET_PROTECTED", exception.Code);
    }

    [Fact]
    public void Rule_creation_accepts_an_ordinary_app()
    {
        var allowed = BlockedApp.Create(
            Guid.NewGuid(), "Editor", @"C:\Program Files\Editor\editor.exe", "editor.exe", Now);
        Assert.Equal("editor.exe", allowed.NormalizedExecutableName);
        Assert.True(allowed.IsEnabled);
    }

    private static void AssertGuardRejects(string? path, string? name)
    {
        var exception = Assert.Throws<DomainException>(() =>
            BlockedApp.EnsureTargetCanBeBlocked(path, name));
        Assert.Equal("BLOCK_TARGET_PROTECTED", exception.Code);
    }
}
