namespace BoundlessStudy.Infrastructure.Services;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

/// <summary>
/// Windows implementation of <see cref="IProcessDiscoveryService"/>.
/// Safely discovers running processes using Win32 and .NET process enumeration.
///
/// Safety and privacy guarantees:
/// - Safely handles Win32Exception, InvalidOperationException, and UnauthorizedAccessException
///   when inspecting protected or system processes (e.g. services.exe, lsass.exe, csrss.exe).
/// - Inaccessible processes are captured with IsAccessible = false and never throw.
/// - Compares candidates against <see cref="BlockedApp.ExcludedExecutableNames"/> (24 protected targets)
///   and <see cref="BlockedApp.EnsureTargetCanBeBlocked"/>.
/// - NEVER terminates, suspends, or injects into any process.
/// - NEVER captures window titles, URLs, command-line arguments, or personal data.
/// - Disposes all Process handles promptly to prevent handle/resource leaks.
/// </summary>
public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, [Out] StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <inheritdoc />
    public IReadOnlyList<ProcessDescriptor> DiscoverProcesses()
    {
        Process[] rawProcesses;
        try
        {
            rawProcesses = Process.GetProcesses();
        }
        catch (Exception)
        {
            return Array.Empty<ProcessDescriptor>();
        }

        var descriptors = new List<ProcessDescriptor>(rawProcesses.Length);

        foreach (var process in rawProcesses)
        {
            try
            {
                using (process)
                {
                    var descriptor = DescribeProcess(process);
                    if (descriptor is not null)
                    {
                        descriptors.Add(descriptor);
                    }
                }
            }
            catch (Exception)
            {
                // Never let a single corrupted, exiting, or restricted process crash enumeration
            }
        }

        return descriptors;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessDescriptor>> DiscoverProcessesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DiscoverProcesses();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ProcessDescriptor? FindProcessById(int processId)
    {
        if (processId < 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return DescribeProcess(process);
        }
        catch (ArgumentException)
        {
            // Process not found or has exited
            return null;
        }
        catch (InvalidOperationException)
        {
            // Process has exited
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessDescriptor> FindProcessesByName(string processOrExecutableName)
    {
        if (string.IsNullOrWhiteSpace(processOrExecutableName))
        {
            return Array.Empty<ProcessDescriptor>();
        }

        var normalizedTarget = NormalizeExecutableName(processOrExecutableName);
        var targetStem = normalizedTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalizedTarget[..^4]
            : normalizedTarget;

        var all = DiscoverProcesses();
        var matches = new List<ProcessDescriptor>();

        foreach (var desc in all)
        {
            if (string.Equals(desc.ExecutableName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(desc);
                continue;
            }

            if (string.Equals(desc.ProcessName, targetStem, StringComparison.OrdinalIgnoreCase)
                || string.Equals(desc.ProcessName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(desc);
            }
        }

        return matches;
    }

    /// <summary>
    /// Safely describes an active <see cref="Process"/> instance.
    /// Catches all expected process inspection exceptions without throwing.
    /// </summary>
    public ProcessDescriptor? DescribeProcess(Process process)
    {
        if (process is null)
        {
            return null;
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            return null;
        }

        string rawName = string.Empty;
        try
        {
            rawName = process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            // Process exited
        }
        catch (Win32Exception)
        {
            // Access denied
        }
        catch (Exception)
        {
        }

        string? executablePath = null;
        bool isAccessible = false;

        // PID 0 is Idle, PID 4 is System - known system virtual processes with no module path
        if (pid > 4)
        {
            executablePath = TryGetExecutablePath(process, pid, out isAccessible);
        }
        else if (pid == 4 && string.IsNullOrWhiteSpace(rawName))
        {
            rawName = "System";
        }
        else if (pid == 0 && string.IsNullOrWhiteSpace(rawName))
        {
            rawName = "Idle";
        }

        return CreateDescriptor(pid, rawName, executablePath, isAccessible);
    }

    /// <summary>
    /// Creates a <see cref="ProcessDescriptor"/> from resolved identity components,
    /// applying normalization and protection checks.
    /// Exposed for testing edge cases and synthetic process scenarios.
    /// </summary>
    public ProcessDescriptor CreateDescriptor(int processId, string? rawProcessName, string? executablePath, bool isAccessible)
    {
        var rawName = rawProcessName?.Trim() ?? string.Empty;
        var validatedPath = ValidateExecutablePath(executablePath);
        isAccessible = isAccessible && validatedPath is not null;

        // Normalization: Executable names must be lowercase, trimmed, with directory stripped, and .exe suffix normalized.
        var nameSource = validatedPath ?? rawName;
        var normalizedExecutableName = NormalizeExecutableName(nameSource);

        // Protected targets check: Compare against BlockedApp.ExcludedExecutableNames (24 protected executables)
        // and BlockedApp.EnsureTargetCanBeBlocked(candidate). Set IsProtected = true on descriptor if matched.
        var isProtected = IsProtectedTarget(validatedPath, normalizedExecutableName)
            || (!string.IsNullOrWhiteSpace(rawName) && IsProtectedTarget(validatedPath, rawName));

        return new ProcessDescriptor(
            ProcessId: processId,
            ProcessName: rawName,
            ExecutableName: normalizedExecutableName,
            ExecutablePath: isAccessible ? validatedPath : null,
            IsProtected: isProtected,
            IsAccessible: isAccessible);
    }

    private static string? ValidateExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var candidate = executablePath.Trim().Trim('"').Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fullPath;
        }
        catch (Exception)
        {
            // Invalid or racing process metadata must remain inaccessible, not fatal.
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsProtectedTarget(string? executablePath, string? executableName)
    {
        // 1. Direct domain logic check
        if (BlockedApp.IsProtectedTarget(executablePath, executableName))
        {
            return true;
        }

        // 2. Direct comparison against the 24 excluded executable names
        if (MatchesExcludedSet(executableName) || MatchesExcludedSet(executablePath))
        {
            return true;
        }

        // 3. Runtime guard check via EnsureTargetCanBeBlocked
        try
        {
            BlockedApp.EnsureTargetCanBeBlocked(executablePath, executableName);
        }
        catch (DomainException ex) when (ex.Code == "BLOCK_TARGET_PROTECTED")
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public string NormalizeExecutableName(string? executableNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(executableNameOrPath))
        {
            return string.Empty;
        }

        var trimmed = executableNameOrPath.Trim().Trim('"').Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // Win32 safe path resolution: strip directory if path separators are present
        var fileName = trimmed.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? trimmed;

        // Win32 strips trailing dots and spaces from file names
        fileName = fileName.Trim().TrimEnd(' ', '.');
        if (fileName.Length == 0)
        {
            return string.Empty;
        }

        var lower = fileName.ToLowerInvariant();

        // .exe suffix normalized: if missing, append .exe
        if (!lower.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            lower += ".exe";
        }

        return lower;
    }

    private static bool MatchesExcludedSet(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var clean = target.Trim().Trim('"').Trim();
        var file = clean.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd(' ', '.') ?? clean;
        var lower = file.ToLowerInvariant();

        if (BlockedApp.ExcludedExecutableNames.Contains(lower))
        {
            return true;
        }

        if (lower.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var stem = lower[..^4];
            if (BlockedApp.ExcludedExecutableNames.Contains(stem))
            {
                return true;
            }
        }
        else
        {
            var withExe = lower + ".exe";
            if (BlockedApp.ExcludedExecutableNames.Contains(withExe))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetExecutablePath(Process process, int pid, out bool isAccessible)
    {
        isAccessible = false;

        // Attempt 1: Standard .NET Process.MainModule
        try
        {
            var mainModule = process.MainModule;
            if (mainModule is not null)
            {
                var fileName = mainModule.FileName;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    isAccessible = true;
                    return fileName;
                }
            }
        }
        catch (Win32Exception)
        {
            // Access is denied (ERROR_ACCESS_DENIED) - standard for system/elevated processes
        }
        catch (InvalidOperationException)
        {
            // Process has exited or 32/64-bit mismatch
        }
        catch (UnauthorizedAccessException)
        {
            // Access unauthorized
        }
        catch (NotSupportedException)
        {
        }
        catch (Exception)
        {
            // Safe fallback
        }

        // Attempt 2: Win32 QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && pid > 0)
        {
            try
            {
                var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        var buffer = new StringBuilder(1024);
                        uint size = (uint)buffer.Capacity;
                        if (QueryFullProcessImageName(hProcess, 0, buffer, ref size) && size > 0)
                        {
                            var path = buffer.ToString();
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                isAccessible = true;
                                return path;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(hProcess);
                    }
                }
            }
            catch (Win32Exception)
            {
            }
            catch (Exception)
            {
            }
        }

        return null;
    }
}
