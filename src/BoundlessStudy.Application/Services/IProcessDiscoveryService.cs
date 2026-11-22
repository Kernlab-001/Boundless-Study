namespace BoundlessStudy.Application.Services;

/// <summary>
/// Service contract for discovering and inspecting running processes on the host operating system.
///
/// Strict behavioral requirements:
/// - Safe enumeration: Never crash on inaccessible, elevated, or rapidly exiting processes.
/// - Process and executable names must be normalized (lowercase, trimmed, directory stripped, .exe normalized).
/// - Protected targets (product components, system-critical processes, accessibility tools) must be identified.
/// - Inaccessible processes must be safely handled with IsAccessible = false without throwing unhandled exceptions.
/// - Must NEVER terminate, suspend, or inject into any process.
/// - Must NEVER capture window titles, URLs, command-line arguments, or personal data.
/// - Fast, low CPU and low memory overhead.
/// </summary>
public interface IProcessDiscoveryService
{
    /// <summary>
    /// Enumerates currently running processes on the system safely.
    /// Inaccessible processes are captured with IsAccessible = false without throwing.
    /// </summary>
    IReadOnlyList<ProcessDescriptor> DiscoverProcesses();

    /// <summary>
    /// Asynchronously enumerates currently running processes on the system safely.
    /// </summary>
    Task<IReadOnlyList<ProcessDescriptor>> DiscoverProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely resolves and describes a specific process by its process ID.
    /// Returns null if the process is not found or has already exited.
    /// </summary>
    ProcessDescriptor? FindProcessById(int processId);

    /// <summary>
    /// Finds all running processes matching the specified process name, executable name, or path.
    /// Matching is case-insensitive and normalizes .exe suffixes.
    /// </summary>
    IReadOnlyList<ProcessDescriptor> FindProcessesByName(string processOrExecutableName);

    /// <summary>
    /// Determines whether the candidate executable name or path matches a protected target.
    /// Compares against <see cref="BoundlessStudy.Domain.Entities.BlockedApp.ExcludedExecutableNames"/>
    /// and uses <see cref="BoundlessStudy.Domain.Entities.BlockedApp.EnsureTargetCanBeBlocked"/>.
    /// </summary>
    bool IsProtectedTarget(string? executablePath, string? executableName);

    /// <summary>
    /// Normalizes an executable name or path: lowercase, trimmed, with directory stripped,
    /// and .exe suffix normalized.
    /// </summary>
    string NormalizeExecutableName(string? executableNameOrPath);
}
