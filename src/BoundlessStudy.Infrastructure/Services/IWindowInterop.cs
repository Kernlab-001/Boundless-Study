using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BoundlessStudy.Infrastructure.Services;

/// <summary>
/// Abstraction for OS windowing operations to enable isolated unit testing.
/// </summary>
public interface IWindowInterop
{
    nint GetForegroundWindowHandle();

    int GetWindowProcessId(nint hWnd);

    (string? ProcessName, string? ExecutablePath) GetProcessDetails(int processId);

}

/// <summary>
/// Real Windows Win32 implementation using User32.dll APIs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32WindowInterop : IWindowInterop
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public nint GetForegroundWindowHandle()
    {
        return GetForegroundWindow();
    }

    public int GetWindowProcessId(nint hWnd)
    {
        if (hWnd == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId((IntPtr)hWnd, out var processId);
        return (int)processId;
    }

    public (string? ProcessName, string? ExecutablePath) GetProcessDetails(int processId)
    {
        if (processId <= 0)
        {
            return (null, null);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            string processName = process.ProcessName;
            string? fileName = null;

            try
            {
                fileName = process.MainModule?.FileName;
            }
            catch
            {
                // Access denied on system/elevated processes or architecture boundary
            }

            return (processName, fileName);
        }
        catch
        {
            return (null, null);
        }
    }

}
