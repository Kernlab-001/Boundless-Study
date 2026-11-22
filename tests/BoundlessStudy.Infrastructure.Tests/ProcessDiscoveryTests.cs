namespace BoundlessStudy.Infrastructure.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Services;
using Xunit;

public sealed class ProcessDiscoveryTests
{
    private readonly WindowsProcessDiscoveryService _service = new();

    [Fact]
    public void DiscoverProcesses_Returns_NonEmpty_List_Of_Valid_Processes()
    {
        var processes = _service.DiscoverProcesses();

        Assert.NotNull(processes);
        Assert.NotEmpty(processes);

        foreach (var desc in processes)
        {
            Assert.True(desc.ProcessId >= 0, $"ProcessId must be non-negative, got {desc.ProcessId}");
            Assert.NotNull(desc.ProcessName);
            Assert.NotNull(desc.ExecutableName);

            // Executable name must be normalized: trimmed, lowercase, no directory, ends with .exe if non-empty
            if (!string.IsNullOrEmpty(desc.ExecutableName))
            {
                Assert.Equal(desc.ExecutableName.Trim(), desc.ExecutableName);
                Assert.Equal(desc.ExecutableName.ToLowerInvariant(), desc.ExecutableName);
                Assert.DoesNotContain("/", desc.ExecutableName);
                Assert.DoesNotContain("\\", desc.ExecutableName);
                Assert.EndsWith(".exe", desc.ExecutableName, StringComparison.OrdinalIgnoreCase);
            }

            // Inaccessible processes must have null path
            if (!desc.IsAccessible)
            {
                Assert.Null(desc.ExecutablePath);
            }
        }
    }

    [Fact]
    public async Task DiscoverProcessesAsync_Returns_Processes_And_Respects_Cancellation()
    {
        var processes = await _service.DiscoverProcessesAsync();
        Assert.NotNull(processes);
        Assert.NotEmpty(processes);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _service.DiscoverProcessesAsync(cts.Token);
        });
    }

    [Fact]
    public void FindProcessById_With_CurrentProcess_Returns_Accurate_Descriptor()
    {
        using var current = Process.GetCurrentProcess();
        var pid = current.Id;

        var descriptor = _service.FindProcessById(pid);

        Assert.NotNull(descriptor);
        Assert.Equal(pid, descriptor.ProcessId);
        Assert.Equal(pid, descriptor.Id);
        Assert.True(descriptor.IsAccessible);
        Assert.NotNull(descriptor.ExecutablePath);
        Assert.True(File.Exists(descriptor.ExecutablePath), $"Executable file should exist: {descriptor.ExecutablePath}");
        Assert.EndsWith(".exe", descriptor.ExecutableName, StringComparison.OrdinalIgnoreCase);
        Assert.False(descriptor.IsProtected);
    }

    [Fact]
    public void FindProcessById_With_Invalid_Or_NonExistent_Pid_Returns_Null()
    {
        Assert.Null(_service.FindProcessById(-1));
        Assert.Null(_service.FindProcessById(-999));
        Assert.Null(_service.FindProcessById(2_000_000_000));
    }

    [Fact]
    public void FindProcessesByName_Matches_By_Stem_And_With_Exe()
    {
        using var current = Process.GetCurrentProcess();
        var rawName = current.ProcessName;

        // Query by raw name (e.g. "testhost" or "dotnet")
        var byStem = _service.FindProcessesByName(rawName);
        Assert.NotEmpty(byStem);
        Assert.Contains(byStem, p => p.ProcessId == current.Id);

        // Query with .exe
        var withExe = _service.FindProcessesByName(rawName + ".exe");
        Assert.NotEmpty(withExe);
        Assert.Contains(withExe, p => p.ProcessId == current.Id);

        // Query non-existent
        var nonExistent = _service.FindProcessesByName("non_existent_process_boundless_test_xyz");
        Assert.Empty(nonExistent);

        // Query blank
        Assert.Empty(_service.FindProcessesByName("   "));
    }

    [Fact]
    public void All_24_ExcludedExecutableNames_Are_Identified_As_Protected()
    {
        // Must contain all 24 protected targets
        Assert.Equal(24, BlockedApp.ExcludedExecutableNames.Count);

        foreach (var target in BlockedApp.ExcludedExecutableNames)
        {
            Assert.True(
                _service.IsProtectedTarget(null, target),
                $"Candidate '{target}' must be identified as protected target.");

            var fakePath = @"C:\Windows\System32\" + target;
            Assert.True(
                _service.IsProtectedTarget(fakePath, target),
                $"Candidate with path '{fakePath}' must be identified as protected target.");

            var desc = _service.CreateDescriptor(1234, target, null, false);
            Assert.True(desc.IsProtected, $"Descriptor for '{target}' must have IsProtected = true.");
        }
    }

    [Theory]
    [InlineData("EXPLORER.EXE")]
    [InlineData("svchost.exe")]
    [InlineData("Svchost.exe")]
    [InlineData("svchost")]
    [InlineData("System")]
    [InlineData("system")]
    [InlineData("dwm.exe")]
    [InlineData("DWM")]
    [InlineData("csrss.exe")]
    [InlineData("services.exe")]
    [InlineData("lsass.exe")]
    [InlineData("narrator.exe")]
    [InlineData("magnify.exe")]
    [InlineData("osk.exe")]
    [InlineData("voiceaccess.exe")]
    [InlineData("livecaptions.exe")]
    [InlineData("atbroker.exe")]
    [InlineData("boundlessstudy.agent.exe")]
    [InlineData("boundlessstudy.desktop.exe")]
    [InlineData("boundlessstudy.service.exe")]
    [InlineData("boundlessstudy.bridge.exe")]
    [InlineData("C:\\Windows\\System32\\svchost.exe")]
    [InlineData("C:/Windows/System32/explorer.exe")]
    [InlineData("  explorer.exe.  ")]
    public void IsProtectedTarget_Identifies_Core_System_And_Accessibility_Targets(string target)
    {
        Assert.True(_service.IsProtectedTarget(null, target));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("chrome.exe")]
    [InlineData("discord.exe")]
    [InlineData("spotify.exe")]
    [InlineData("slack.exe")]
    [InlineData("code.exe")]
    [InlineData("steam.exe")]
    [InlineData("C:\\Program Files\\Notepad++\\notepad++.exe")]
    public void Unprotected_Applications_Are_Not_Marked_As_Protected(string target)
    {
        Assert.False(_service.IsProtectedTarget(null, target));
        var desc = _service.CreateDescriptor(9999, target, @"C:\Tools\" + target, true);
        Assert.False(desc.IsProtected);
    }

    [Fact]
    public void Inaccessible_Process_Simulation_Never_Throws_And_Preserves_Identity()
    {
        // Simulate a protected system process where module inspection was denied
        var desc = _service.CreateDescriptor(
            processId: 654,
            rawProcessName: "services",
            executablePath: null,
            isAccessible: false);

        Assert.Equal(654, desc.ProcessId);
        Assert.Equal("services", desc.ProcessName);
        Assert.Equal("services.exe", desc.ExecutableName);
        Assert.Null(desc.ExecutablePath);
        Assert.False(desc.IsAccessible);
        Assert.True(desc.IsProtected);
    }

    [Theory]
    [InlineData("relative\\game.exe")]
    [InlineData("C:\\Games\\game.dll")]
    [InlineData("not a path")]
    public void CreateDescriptor_RejectsUnqualifiedOrNonExecutablePaths(string path)
    {
        var descriptor = _service.CreateDescriptor(1234, "game", path, isAccessible: true);

        Assert.False(descriptor.IsAccessible);
        Assert.Null(descriptor.ExecutablePath);
        Assert.Equal("game.exe", descriptor.ExecutableName);
    }

    [Fact]
    public void CreateDescriptor_NormalizesQualifiedExecutablePath()
    {
        var descriptor = _service.CreateDescriptor(1234, "game", @"C:\Games\..\Apps\game.exe", isAccessible: true);

        Assert.True(descriptor.IsAccessible);
        Assert.Equal(Path.GetFullPath(@"C:\Games\..\Apps\game.exe"), descriptor.ExecutablePath);
        Assert.Equal("game.exe", descriptor.ExecutableName);
    }

    [Fact]
    public void System_And_Idle_Virtual_Pids_Are_Safely_Handled()
    {
        var idleDesc = _service.CreateDescriptor(0, "Idle", null, false);
        Assert.Equal(0, idleDesc.ProcessId);
        Assert.Equal("Idle", idleDesc.ProcessName);
        Assert.Equal("idle.exe", idleDesc.ExecutableName);
        Assert.False(idleDesc.IsAccessible);

        var systemDesc = _service.CreateDescriptor(4, "System", null, false);
        Assert.Equal(4, systemDesc.ProcessId);
        Assert.Equal("System", systemDesc.ProcessName);
        Assert.Equal("system.exe", systemDesc.ExecutableName);
        Assert.False(systemDesc.IsAccessible);
        Assert.True(systemDesc.IsProtected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("notepad", "notepad.exe")]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("NOTEPAD.EXE", "notepad.exe")]
    [InlineData("C:\\Windows\\System32\\notepad.exe", "notepad.exe")]
    [InlineData("C:/Windows/System32/calc.exe", "calc.exe")]
    [InlineData("  C:\\App\\app.exe.  ", "app.exe")]
    [InlineData("\"C:\\Games\\Doom.exe\"", "doom.exe")]
    [InlineData("sub_folder/tool", "tool.exe")]
    public void NormalizeExecutableName_Applies_Strict_Normalization_Rules(string? input, string expected)
    {
        var actual = _service.NormalizeExecutableName(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProcessDescriptor_Privacy_Guarantee_Contains_No_Window_Or_Personal_Data()
    {
        var properties = typeof(ProcessDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant())
            .ToArray();

        var forbiddenSubstrings = new[]
        {
            "window", "title", "url", "argument", "cmd", "commandline", "user", "browser", "document"
        };

        foreach (var prop in properties)
        {
            foreach (var forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, prop);
            }
        }
    }

    [Fact]
    public void ProcessDiscovery_Never_Terminates_Or_Modifies_Processes()
    {
        // Start a benign worker process
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 5 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        try
        {
            // Run discovery while it is running
            var discovered = _service.FindProcessById(process.Id);

            Assert.NotNull(discovered);
            Assert.Equal(process.Id, discovered.ProcessId);

            // Verify process is still alive and was NOT terminated or modified
            Assert.False(process.HasExited, "Process must NOT be terminated by discovery.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void DiscoverProcesses_Executes_Rapidly_Under_Performance_Budget()
    {
        // Pre-warm JIT
        _service.DiscoverProcesses();

        var sw = Stopwatch.StartNew();
        var processes = _service.DiscoverProcesses();
        sw.Stop();

        Assert.NotEmpty(processes);
        // Entire enumeration of all system processes should be very fast (< 1000ms budget)
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"DiscoverProcesses took {sw.ElapsedMilliseconds}ms, exceeding 1000ms performance budget.");
    }
}
