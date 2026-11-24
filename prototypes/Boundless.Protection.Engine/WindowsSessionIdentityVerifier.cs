using System.Runtime.InteropServices;

namespace Boundless.Protection.Engine;

/// <summary>
/// Verifies caller-supplied session claims against the current Windows process token
/// (user SID, logon AuthenticationId LUID, and Windows session ID).
/// </summary>
public sealed class WindowsSessionIdentityVerifier
{
    internal delegate bool TokenIdentityReader(out WindowsTokenSessionIdentity identity, out string error);

    private static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);

    private readonly Func<bool> _isWindows;
    private readonly TokenIdentityReader _readTokenIdentity;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _sessionLifetime;

    public WindowsSessionIdentityVerifier()
        : this(
            OperatingSystem.IsWindows,
            TryReadCurrentProcessTokenIdentity,
            () => DateTimeOffset.UtcNow,
            DefaultSessionLifetime)
    {
    }

    internal WindowsSessionIdentityVerifier(
        Func<bool> isWindows,
        TokenIdentityReader readTokenIdentity,
        Func<DateTimeOffset> utcNow,
        TimeSpan? sessionLifetime = null)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _readTokenIdentity = readTokenIdentity ?? throw new ArgumentNullException(nameof(readTokenIdentity));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

        var lifetime = sessionLifetime ?? DefaultSessionLifetime;
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime), "Session lifetime must be positive.");
        _sessionLifetime = lifetime;
    }

    public IdentityEvidenceSourceTrust SourceTrust => IdentityEvidenceSourceTrust.OperatingSystemVerified;

    public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
        CallerSessionIdentityClaims claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isWindows())
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Unsupported(
                    "platform-unsupported-windows-identity-required"));
        }

        if (claims.SessionId == Guid.Empty)
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Rejected("invalid-session-id"));
        }

        if (claims.SessionGeneration <= 0)
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Rejected("invalid-session-generation"));
        }

        if (!_readTokenIdentity(out var osIdentity, out var error))
        {
            var reason = string.IsNullOrWhiteSpace(error) ? "unknown OS token error" : error;
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Unavailable(
                    $"os-identity-unavailable: {reason}"));
        }

        if (string.IsNullOrWhiteSpace(osIdentity.UserSid)
            || string.IsNullOrWhiteSpace(osIdentity.AuthenticationId))
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Unavailable(
                    "os-identity-unavailable: OS token returned incomplete identity attributes"));
        }

        if (string.IsNullOrWhiteSpace(claims.SubjectSid)
            || !string.Equals(claims.SubjectSid, osIdentity.UserSid, StringComparison.Ordinal))
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Rejected("session-subject-sid-mismatch"));
        }

        if (claims.AuthenticationContext is not null
            && !string.Equals(claims.AuthenticationContext, osIdentity.AuthenticationId, StringComparison.Ordinal))
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Rejected("session-auth-context-mismatch"));
        }

        var authenticatedAtUtc = _utcNow().ToUniversalTime();
        var expiresAtUtc = authenticatedAtUtc.Add(_sessionLifetime);
        var verified = new VerifiedSessionIdentity(
            claims.SessionId,
            claims.SessionGeneration,
            osIdentity.UserSid,
            osIdentity.AuthenticationId,
            authenticatedAtUtc,
            authenticatedAtUtc,
            expiresAtUtc);

        return Task.FromResult(IdentityVerificationResult<VerifiedSessionIdentity>.Verified(verified));
    }

    internal static bool TryReadCurrentProcessTokenIdentity(out WindowsTokenSessionIdentity identity, out string error)
    {
        if (!WindowsSecurityApis.OpenProcessToken(
                WindowsSecurityApis.GetCurrentProcess(),
                WindowsSecurityApis.TOKEN_QUERY,
                out var tokenHandle))
        {
            var win32Error = Marshal.GetLastWin32Error();
            identity = default;
            error = $"OpenProcessToken failed with Win32 error {win32Error}";
            return false;
        }

        using (tokenHandle)
        {
            if (tokenHandle.IsInvalid)
            {
                identity = default;
                error = "OpenProcessToken returned an invalid handle";
                return false;
            }

            if (!TryGetTokenUserSid(tokenHandle, out var userSid, out error))
            {
                identity = default;
                return false;
            }

            if (!TryGetTokenAuthenticationId(tokenHandle, out var authenticationId, out error))
            {
                identity = default;
                return false;
            }

            if (!TryGetTokenSessionId(tokenHandle, out var sessionId, out error))
            {
                identity = default;
                return false;
            }

            identity = new WindowsTokenSessionIdentity(userSid, authenticationId, sessionId);
            error = string.Empty;
            return true;
        }
    }

    private static bool TryGetTokenUserSid(
        SafeProcessTokenHandle tokenHandle,
        out string userSid,
        out string error)
    {
        var firstCallSucceeded = WindowsSecurityApis.GetTokenInformation(
            tokenHandle,
            WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenUser,
            IntPtr.Zero,
            0,
            out var requiredBytes);

        var lastError = Marshal.GetLastWin32Error();
        if (firstCallSucceeded || lastError != WindowsSecurityApis.ERROR_INSUFFICIENT_BUFFER || requiredBytes == 0)
        {
            userSid = string.Empty;
            error = $"GetTokenInformation(TokenUser) size probe failed with Win32 error {lastError}";
            return false;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!WindowsSecurityApis.GetTokenInformation(
                    tokenHandle,
                    WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenUser,
                    buffer,
                    requiredBytes,
                    out _))
            {
                var readError = Marshal.GetLastWin32Error();
                userSid = string.Empty;
                error = $"GetTokenInformation(TokenUser) failed with Win32 error {readError}";
                return false;
            }

            var tokenUser = Marshal.PtrToStructure<WindowsSecurityApis.TOKEN_USER>(buffer);
            if (tokenUser.User.Sid == IntPtr.Zero)
            {
                userSid = string.Empty;
                error = "GetTokenInformation(TokenUser) returned a null SID pointer";
                return false;
            }

            if (!WindowsSecurityApis.ConvertSidToStringSidW(tokenUser.User.Sid, out var sidHandle))
            {
                var sidError = Marshal.GetLastWin32Error();
                userSid = string.Empty;
                error = $"ConvertSidToStringSidW failed with Win32 error {sidError}";
                return false;
            }

            using (sidHandle)
            {
                if (sidHandle.IsInvalid)
                {
                    userSid = string.Empty;
                    error = "ConvertSidToStringSidW returned an invalid string handle";
                    return false;
                }

                var sidString = Marshal.PtrToStringUni(sidHandle.DangerousGetHandle());
                if (string.IsNullOrWhiteSpace(sidString))
                {
                    userSid = string.Empty;
                    error = "ConvertSidToStringSidW returned an empty SID string";
                    return false;
                }

                userSid = sidString;
                error = string.Empty;
                return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenAuthenticationId(
        SafeProcessTokenHandle tokenHandle,
        out string authenticationId,
        out string error)
    {
        var bufferSize = Marshal.SizeOf<WindowsSecurityApis.TOKEN_STATISTICS>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!WindowsSecurityApis.GetTokenInformation(
                    tokenHandle,
                    WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenStatistics,
                    buffer,
                    (uint)bufferSize,
                    out _))
            {
                var win32Error = Marshal.GetLastWin32Error();
                authenticationId = string.Empty;
                error = $"GetTokenInformation(TokenStatistics) failed with Win32 error {win32Error}";
                return false;
            }

            var statistics = Marshal.PtrToStructure<WindowsSecurityApis.TOKEN_STATISTICS>(buffer);
            var luidString = statistics.AuthenticationId.ToString();
            if (string.IsNullOrWhiteSpace(luidString))
            {
                authenticationId = string.Empty;
                error = "GetTokenInformation(TokenStatistics) returned an empty AuthenticationId";
                return false;
            }

            authenticationId = luidString;
            error = string.Empty;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenSessionId(
        SafeProcessTokenHandle tokenHandle,
        out uint sessionId,
        out string error)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!WindowsSecurityApis.GetTokenInformation(
                    tokenHandle,
                    WindowsSecurityApis.TOKEN_INFORMATION_CLASS.TokenSessionId,
                    buffer,
                    sizeof(uint),
                    out _))
            {
                var win32Error = Marshal.GetLastWin32Error();
                sessionId = 0;
                error = $"GetTokenInformation(TokenSessionId) failed with Win32 error {win32Error}";
                return false;
            }

            sessionId = unchecked((uint)Marshal.ReadInt32(buffer));
            error = string.Empty;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal readonly record struct WindowsTokenSessionIdentity(
    string UserSid,
    string AuthenticationId,
    uint SessionId);
