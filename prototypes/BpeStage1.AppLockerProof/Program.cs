// BPE Stage 1 — AppLocker Enforcement Proof Harness
// Standalone prototype (outside production src/, tests/, and BoundlessStudy.slnx).
// Runs ONLY inside a disposable CI environment (GitHub Actions windows-2025).
// Launches a target executable as a non-admin local user under an active AppLocker
// deny policy and captures synchronous Win32 error 1260 (0x800704EC) or exit status.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;

[assembly: SupportedOSPlatform("windows")]

namespace BpeStage1.AppLockerProof;

internal static class Program
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = 1260; // 0x800704EC
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    private const int LOGON_WITH_PROFILE = 0x00000001;
    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint INFINITE_WAIT_TIMEOUT_MS = 30_000;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessWithLogonW(
        string lpUsername,
        string? lpDomain,
        string lpPassword,
        int dwLogonFlags,
        string? lpApplicationName,
        string? lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static int Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "Usage: BpeStage1.AppLockerProof <exePath> <username> <password> <resultJsonPath> <expectBlocked:true|false> [markerJsonPath]");
            return 1;
        }

        string exePath = args[0];
        string username = args[1];
        string password = args[2];
        string resultJsonPath = args[3];
        bool expectBlocked = string.Equals(args[4], "true", StringComparison.OrdinalIgnoreCase);
        string? markerPath = args.Length > 5 && !string.IsNullOrWhiteSpace(args[5]) ? args[5] : null;

        if (markerPath is not null && File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        string? commandLine = markerPath is not null
            ? $"\"{exePath}\" --marker \"{markerPath}\""
            : $"\"{exePath}\"";

        string? workingDir = Path.GetDirectoryName(exePath);

        Console.WriteLine($"[BPE-Stage1] Target: {exePath}");
        Console.WriteLine($"[BPE-Stage1] User: {username} | ExpectBlocked: {expectBlocked}");
        if (markerPath is not null)
        {
            Console.WriteLine($"[BPE-Stage1] Marker path: {markerPath}");
        }

        var result = new LaunchResult
        {
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            TargetPath = exePath,
            Username = username,
            ExpectBlocked = expectBlocked,
            MarkerPath = markerPath
        };

        try
        {
            using var currentIdentity = WindowsIdentity.GetCurrent();
            bool isSystem = currentIdentity.IsSystem;

            bool created = false;
            int win32Error = 0;
            PROCESS_INFORMATION pi = default;

            if (!isSystem)
            {
                result.LaunchApi = "CreateProcessWithLogonW";
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                created = CreateProcessWithLogonW(
                    username,
                    ".",
                    password,
                    LOGON_WITH_PROFILE,
                    exePath,
                    commandLine,
                    CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero,
                    workingDir,
                    ref si,
                    out pi);

                win32Error = created ? 0 : Marshal.GetLastWin32Error();
                Console.WriteLine($"[BPE-Stage1] CreateProcessWithLogonW => created={created}, win32Error={win32Error}");
            }

            if (isSystem || (!created && (win32Error == ERROR_ACCESS_DENIED || win32Error == ERROR_PRIVILEGE_NOT_HELD)))
            {
                Console.WriteLine("[BPE-Stage1] Trying fallback LogonUserW + CreateProcessAsUserW...");
                result.LaunchApi = "LogonUserW+CreateProcessAsUserW";

                if (!LogonUserW(username, ".", password, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out IntPtr userToken))
                {
                    int logonErr = Marshal.GetLastWin32Error();
                    result.Phase = "LogonUserW";
                    result.Win32Error = logonErr;
                    result.Win32HResult = $"0x{unchecked((uint)Marshal.GetHRForLastWin32Error()):X8}";
                    result.ErrorMessage = $"LogonUserW failed: Win32 error {logonErr} ({new Win32Exception(logonErr).Message})";
                    result.Success = false;
                    WriteResult(result, resultJsonPath);
                    return 2;
                }

                try
                {
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                    created = CreateProcessAsUserW(
                        userToken,
                        exePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CREATE_NO_WINDOW,
                        IntPtr.Zero,
                        workingDir,
                        ref si,
                        out pi);

                    win32Error = created ? 0 : Marshal.GetLastWin32Error();
                    Console.WriteLine($"[BPE-Stage1] CreateProcessAsUserW => created={created}, win32Error={win32Error}");
                }
                finally
                {
                    CloseHandle(userToken);
                }
            }

            result.Phase = result.LaunchApi;
            result.Win32Error = win32Error;
            result.ProcessCreated = created;

            if (!created)
            {
                result.Win32HResult = win32Error != 0 ? $"0x{0x80070000u | (uint)win32Error:X8}" : "0x00000000";
                string win32Message = new Win32Exception(win32Error).Message;

                if (win32Error == ERROR_ACCESS_DISABLED_BY_POLICY)
                {
                    result.AppLockerBlocked = true;
                    result.Success = expectBlocked;
                    result.ErrorMessage = $"AppLocker BLOCKED execution (Win32 {win32Error} / {result.Win32HResult}: {win32Message})";
                    Console.WriteLine($"[BPE-Stage1] *** {result.ErrorMessage} ***");
                }
                else
                {
                    result.AppLockerBlocked = false;
                    result.Success = false;
                    result.ErrorMessage = $"Process launch failed with unexpected Win32 error {win32Error} ({result.Win32HResult}: {win32Message})";
                }
            }
            else
            {
                result.ProcessId = pi.dwProcessId;
                WaitForSingleObject(pi.hProcess, INFINITE_WAIT_TIMEOUT_MS);
                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                result.ProcessExitCode = unchecked((int)exitCode);

                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);

                result.AppLockerBlocked = false;
                result.Success = !expectBlocked && exitCode == 0;
                result.ErrorMessage = expectBlocked
                    ? $"Process launched unexpectedly (PID {pi.dwProcessId}, exit {result.ProcessExitCode}) when AppLocker block was expected"
                    : $"Process launched cleanly (PID {pi.dwProcessId}, exit {result.ProcessExitCode})";
                Console.WriteLine($"[BPE-Stage1] {result.ErrorMessage}");
            }

            if (markerPath is not null && File.Exists(markerPath))
            {
                result.MarkerExists = true;
                result.MarkerContent = File.ReadAllText(markerPath);
            }
        }
        catch (Exception ex)
        {
            result.Phase = "Exception";
            result.Success = false;
            result.ErrorMessage = $"Unhandled exception: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[BPE-Stage1] EXCEPTION: {ex}");
        }

        WriteResult(result, resultJsonPath);
        return result.Success ? 0 : 3;
    }

    private static void WriteResult(LaunchResult result, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(path, json);
        Console.WriteLine($"[BPE-Stage1] Result JSON written to: {path}");
        Console.WriteLine(json);
    }

    private sealed class LaunchResult
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public bool ExpectBlocked { get; set; }
        public string LaunchApi { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public int Win32Error { get; set; }
        public string Win32HResult { get; set; } = string.Empty;
        public bool ProcessCreated { get; set; }
        public int? ProcessId { get; set; }
        public int? ProcessExitCode { get; set; }
        public bool AppLockerBlocked { get; set; }
        public string? MarkerPath { get; set; }
        public bool MarkerExists { get; set; }
        public string? MarkerContent { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
