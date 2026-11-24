using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Boundless.Protection.Engine;

/// <summary>
/// Verifies caller-supplied process claims against a live Windows process handle
/// and its process token, binding the PID to its kernel creation timestamp and
/// owner SID and re-verifying the same handle before issuing evidence.
/// </summary>
public sealed class WindowsProcessIdentityVerifier
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMilliseconds(1);

    private readonly IWindowsProcessProbe _probe;
    private readonly Func<bool> _isWindows;
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsProcessIdentityVerifier()
        : this(Win32WindowsProcessProbe.Instance, OperatingSystem.IsWindows, () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsProcessIdentityVerifier(
        IWindowsProcessProbe probe,
        Func<bool> isWindows,
        Func<DateTimeOffset> utcNow)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public IdentityEvidenceSourceTrust SourceTrust => IdentityEvidenceSourceTrust.OperatingSystemVerified;

    public Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
        CallerProcessIdentityClaims claims,
        VerifiedSessionIdentity session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VerifyProcess(claims, session));
    }

    public IdentityVerificationResult<VerifiedProcessIdentity> VerifyProcess(
        CallerProcessIdentityClaims claims,
        VerifiedSessionIdentity session)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(session);

        if (!_isWindows())
        {
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unsupported(
                "platform-unsupported-windows-identity-required");
        }

        if (claims.ProcessId <= 0)
        {
            return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected("invalid-process-id");
        }

        if (session.SessionId == Guid.Empty || session.Generation <= 0)
        {
            return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected("invalid-session-identity");
        }

        if (!_probe.TryOpenProcess(claims.ProcessId, out var processHandle, out var openError)
            || processHandle is null)
        {
            return openError switch
            {
                WindowsSecurityApis.ERROR_ACCESS_DENIED =>
                    IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-access-denied"),
                WindowsSecurityApis.ERROR_INVALID_PARAMETER =>
                    IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-not-found"),
                _ => IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                    $"process-open-failed: {openError}")
            };
        }

        using (processHandle)
        {
            if (!_probe.TryGetProcessTimes(
                    processHandle,
                    out var actualStartUtc,
                    out var hasExitTime,
                    out var timesError))
            {
                return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                    $"process-times-failed: {timesError}");
            }

            actualStartUtc = actualStartUtc.ToUniversalTime();
            var claimedStartUtc = claims.ProcessStartUtc.ToUniversalTime();
            var startDeltaTicks = Math.Abs((actualStartUtc.UtcDateTime - claimedStartUtc.UtcDateTime).Ticks);
            if (startDeltaTicks > StartTimeTolerance.Ticks)
            {
                return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected(
                    "process-pid-reused-or-start-time-mismatch");
            }

            var livenessFailure = CheckProcessLiveness(processHandle, hasExitTime, isRecheck: false);
            if (livenessFailure is not null)
                return livenessFailure;

            if (!_probe.TryOpenProcessToken(processHandle, out var tokenHandle, out var tokenOpenError)
                || tokenHandle is null)
            {
                return tokenOpenError == WindowsSecurityApis.ERROR_ACCESS_DENIED
                    ? IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-token-access-denied")
                    : IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                        $"process-token-open-failed: {tokenOpenError}");
            }

            string? actualSid;
            using (tokenHandle)
            {
                if (!_probe.TryGetTokenUserSid(tokenHandle, out actualSid, out var tokenUserError)
                    || string.IsNullOrWhiteSpace(actualSid))
                {
                    return tokenUserError == WindowsSecurityApis.ERROR_ACCESS_DENIED
                        ? IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-token-access-denied")
                        : IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                            $"process-token-query-failed: {tokenUserError}");
                }

                if (!string.Equals(actualSid, session.SubjectSid, StringComparison.Ordinal)
                    || !string.Equals(actualSid, claims.SubjectSid, StringComparison.Ordinal))
                {
                    return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected(
                        "process-owner-sid-mismatch");
                }

                if (!_probe.TryGetTokenSessionId(tokenHandle, out var tokenSessionId, out var tokenSessionError))
                {
                    return tokenSessionError == WindowsSecurityApis.ERROR_ACCESS_DENIED
                        ? IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-token-access-denied")
                        : IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                            $"process-token-session-query-failed: {tokenSessionError}");
                }

                if (!_probe.TryProcessIdToSessionId(claims.ProcessId, out var pidSessionId, out var pidSessionError))
                {
                    return pidSessionError switch
                    {
                        WindowsSecurityApis.ERROR_ACCESS_DENIED =>
                            IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-access-denied"),
                        WindowsSecurityApis.ERROR_INVALID_PARAMETER =>
                            IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-not-found"),
                        _ => IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                            $"process-session-query-failed: {pidSessionError}")
                    };
                }

                if (pidSessionId != tokenSessionId)
                {
                    return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected(
                        "process-pid-reused-or-session-mismatch");
                }
            }

            // Re-verify on the exact same handle to guarantee no exit or replacement occurred.
            if (!_probe.TryGetProcessTimes(
                    processHandle,
                    out var recheckStartUtc,
                    out var recheckExitTime,
                    out var recheckTimesError))
            {
                return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(
                    $"process-times-recheck-failed: {recheckTimesError}");
            }

            if (recheckStartUtc.ToUniversalTime() != actualStartUtc)
            {
                return IdentityVerificationResult<VerifiedProcessIdentity>.Rejected(
                    "process-pid-reused-or-start-time-mismatch");
            }

            var recheckLivenessFailure = CheckProcessLiveness(processHandle, recheckExitTime, isRecheck: true);
            if (recheckLivenessFailure is not null)
                return recheckLivenessFailure;

            var observedAtUtc = _utcNow().ToUniversalTime();
            if (observedAtUtc < actualStartUtc)
                observedAtUtc = actualStartUtc;

            var evidence = new VerifiedProcessIdentity(
                claims.ProcessId,
                actualStartUtc,
                actualSid,
                claims.ExecutablePath ?? string.Empty,
                claims.ExecutableSha256 ?? string.Empty,
                session.SessionId,
                session.Generation,
                observedAtUtc);

            return IdentityVerificationResult<VerifiedProcessIdentity>.Verified(evidence);
        }
    }

    private IdentityVerificationResult<VerifiedProcessIdentity>? CheckProcessLiveness(
        IProcessHandle processHandle,
        bool hasExitTime,
        bool isRecheck)
    {
        if (hasExitTime)
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-already-exited");

        if (!_probe.TryGetExitCode(processHandle, out var exitCode, out var exitError))
        {
            var prefix = isRecheck ? "process-exit-code-recheck-failed" : "process-exit-code-failed";
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable($"{prefix}: {exitError}");
        }

        if (exitCode != WindowsSecurityApis.STILL_ACTIVE)
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-already-exited");

        var waitResult = _probe.WaitForExitZero(processHandle, out var waitError);
        if (waitResult == WindowsSecurityApis.WAIT_OBJECT_0)
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable("process-already-exited");

        if (waitResult == WindowsSecurityApis.WAIT_FAILED
            && waitError != WindowsSecurityApis.ERROR_ACCESS_DENIED)
        {
            var prefix = isRecheck ? "process-wait-recheck-failed" : "process-wait-failed";
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable($"{prefix}: {waitError}");
        }

        return null;
    }

    internal interface IProcessHandle : IDisposable
    {
    }

    internal interface IProcessTokenHandle : IDisposable
    {
    }

    internal interface IWindowsProcessProbe
    {
        bool TryOpenProcess(int processId, out IProcessHandle? handle, out int win32Error);
        bool TryGetProcessTimes(IProcessHandle handle, out DateTimeOffset creationTimeUtc, out bool hasExitTime, out int win32Error);
        bool TryGetExitCode(IProcessHandle handle, out uint exitCode, out int win32Error);
        uint WaitForExitZero(IProcessHandle handle, out int win32Error);
        bool TryOpenProcessToken(IProcessHandle processHandle, out IProcessTokenHandle? tokenHandle, out int win32Error);
        bool TryGetTokenUserSid(IProcessTokenHandle tokenHandle, out string? sid, out int win32Error);
        bool TryGetTokenSessionId(IProcessTokenHandle tokenHandle, out uint sessionId, out int win32Error);
        bool TryProcessIdToSessionId(int processId, out uint sessionId, out int win32Error);
    }

    private sealed class Win32WindowsProcessProbe : IWindowsProcessProbe
    {
        public static readonly Win32WindowsProcessProbe Instance = new();

        private Win32WindowsProcessProbe()
        {
        }

        public bool TryOpenProcess(int processId, out IProcessHandle? handle, out int win32Error)
        {
            var nativeHandle = WindowsSecurityApis.OpenProcess(
                WindowsSecurityApis.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);

            if (nativeHandle.IsInvalid)
            {
                win32Error = Marshal.GetLastWin32Error();
                nativeHandle.Dispose();
                handle = null;
                return false;
            }

            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            handle = new NativeProcessHandle(nativeHandle);
            return true;
        }

        public bool TryGetProcessTimes(
            IProcessHandle handle,
            out DateTimeOffset creationTimeUtc,
            out bool hasExitTime,
            out int win32Error)
        {
            var native = ((NativeProcessHandle)handle).Handle;
            if (!WindowsSecurityApis.GetProcessTimes(
                    native,
                    out var creationTime,
                    out var exitTime,
                    out _,
                    out _))
            {
                win32Error = Marshal.GetLastWin32Error();
                creationTimeUtc = default;
                hasExitTime = false;
                return false;
            }

            creationTimeUtc = creationTime.ToDateTimeOffset().ToUniversalTime();
            hasExitTime = exitTime.dwLowDateTime != 0 || exitTime.dwHighDateTime != 0;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetExitCode(IProcessHandle handle, out uint exitCode, out int win32Error)
        {
            var native = ((NativeProcessHandle)handle).Handle;
            if (!WindowsSecurityApis.GetExitCodeProcess(native, out exitCode))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public uint WaitForExitZero(IProcessHandle handle, out int win32Error)
        {
            var native = ((NativeProcessHandle)handle).Handle;
            var result = WindowsSecurityApis.WaitForSingleObject(native, 0);
            win32Error = result == WindowsSecurityApis.WAIT_FAILED
                ? Marshal.GetLastWin32Error()
                : WindowsSecurityApis.ERROR_SUCCESS;
            return result;
        }

        public bool TryOpenProcessToken(
            IProcessHandle processHandle,
            out IProcessTokenHandle? tokenHandle,
            out int win32Error)
        {
            var native = ((NativeProcessHandle)processHandle).Handle;
            var addedRef = false;
            try
            {
                native.DangerousAddRef(ref addedRef);
                if (!WindowsSecurityApis.OpenProcessToken(
                        native.DangerousGetHandle(),
                        WindowsSecurityApis.TOKEN_QUERY,
                        out var nativeToken))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    nativeToken?.Dispose();
                    tokenHandle = null;
                    return false;
                }

                if (nativeToken.IsInvalid)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    nativeToken.Dispose();
                    tokenHandle = null;
                    return false;
                }

                win32Error = WindowsSecurityApis.ERROR_SUCCESS;
                tokenHandle = new NativeProcessTokenHandle(nativeToken);
                return true;
            }
            finally
            {
                if (addedRef)
                    native.DangerousRelease();
            }
        }

        public bool TryGetTokenUserSid(IProcessTokenHandle tokenHandle, out string? sid, out int win32Error)
        {
            var nativeToken = ((NativeProcessTokenHandle)tokenHandle).Handle;
            WindowsSecurityApis.GetTokenInformation(
                nativeToken,
                WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenUser,
                IntPtr.Zero,
                0,
                out var requiredSize);

            var queryError = Marshal.GetLastWin32Error();
            if (queryError != WindowsSecurityApis.ERROR_INSUFFICIENT_BUFFER || requiredSize == 0)
            {
                win32Error = queryError;
                sid = null;
                return false;
            }

            var buffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                if (!WindowsSecurityApis.GetTokenInformation(
                        nativeToken,
                        WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenUser,
                        buffer,
                        requiredSize,
                        out _))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    sid = null;
                    return false;
                }

                var tokenUser = Marshal.PtrToStructure<WindowsSecurityApis.TOKEN_USER>(buffer);
                if (!WindowsSecurityApis.ConvertSidToStringSidW(tokenUser.User.Sid, out var sidHandle)
                    || sidHandle.IsInvalid)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    sidHandle?.Dispose();
                    sid = null;
                    return false;
                }

                using (sidHandle)
                {
                    sid = Marshal.PtrToStringUni(sidHandle.DangerousGetHandle());
                    win32Error = WindowsSecurityApis.ERROR_SUCCESS;
                    return !string.IsNullOrWhiteSpace(sid);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool TryGetTokenSessionId(IProcessTokenHandle tokenHandle, out uint sessionId, out int win32Error)
        {
            var nativeToken = ((NativeProcessTokenHandle)tokenHandle).Handle;
            var size = (uint)sizeof(uint);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!WindowsSecurityApis.GetTokenInformation(
                        nativeToken,
                        WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenSessionId,
                        buffer,
                        size,
                        out _))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    sessionId = 0;
                    return false;
                }

                sessionId = unchecked((uint)Marshal.ReadInt32(buffer));
                win32Error = WindowsSecurityApis.ERROR_SUCCESS;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool TryProcessIdToSessionId(int processId, out uint sessionId, out int win32Error)
        {
            if (!WindowsSecurityApis.ProcessIdToSessionId(unchecked((uint)processId), out sessionId))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        private sealed class NativeProcessHandle(SafeProcessHandle handle) : IProcessHandle
        {
            public SafeProcessHandle Handle { get; } = handle;
            public void Dispose() => Handle.Dispose();
        }

        private sealed class NativeProcessTokenHandle(SafeProcessTokenHandle handle) : IProcessTokenHandle
        {
            public SafeProcessTokenHandle Handle { get; } = handle;
            public void Dispose() => Handle.Dispose();
        }
    }
}
