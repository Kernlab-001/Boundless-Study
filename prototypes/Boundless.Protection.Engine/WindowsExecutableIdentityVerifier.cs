using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Boundless.Protection.Engine;

/// <summary>
/// Operating-system verified executable file identity bound to a single Win32 handle.
/// Constructor is internal so only trusted OS verification code in this assembly can issue it.
/// </summary>
public sealed class VerifiedExecutableIdentity
{
    internal VerifiedExecutableIdentity(
        string executablePath,
        string sha256,
        uint volumeSerialNumber,
        ulong fileIndex,
        ulong? extendedVolumeSerialNumber,
        string? fileId128,
        long fileSizeBytes,
        DateTimeOffset observedAtUtc)
    {
        ExecutablePath = executablePath;
        Sha256 = sha256;
        VolumeSerialNumber = volumeSerialNumber;
        FileIndex = fileIndex;
        ExtendedVolumeSerialNumber = extendedVolumeSerialNumber;
        FileId128 = fileId128;
        FileSizeBytes = fileSizeBytes;
        ObservedAtUtc = observedAtUtc;
    }

    public string ExecutablePath { get; }
    public string Sha256 { get; }
    public uint VolumeSerialNumber { get; }
    public ulong FileIndex { get; }
    public ulong? ExtendedVolumeSerialNumber { get; }
    public string? FileId128 { get; }
    public long FileSizeBytes { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>
/// Resolves and verifies authoritative Windows executable file identity from a single
/// Win32 file handle so path normalization, file metadata, and SHA-256 hashing cannot
/// suffer from TOCTOU replacement.
/// </summary>
public sealed class WindowsExecutableIdentityVerifier
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const uint FileAttributeDirectory = 0x00000010;
    private const int InitialPathCapacity = 512;

    private readonly Func<bool> _isWindows;
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsExecutableIdentityVerifier()
        : this(OperatingSystem.IsWindows, () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsExecutableIdentityVerifier(Func<bool> isWindows, Func<DateTimeOffset> utcNow)
    {
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public IdentityVerificationResult<VerifiedExecutableIdentity> Resolve(string filePath)
    {
        if (!TryOpenAndInspect(filePath, out SafeFileHandle? hFile, out string canonicalPath,
                out var fileInfo, out var extendedVolumeSerial, out var fileId128, out var failureResult))
        {
            return failureResult!;
        }

        using (hFile)
        {
            string sha256;
            using var stream = new FileStream(hFile, FileAccess.Read);
            try
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (UnauthorizedAccessException)
            {
                return IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-access-denied");
            }
            catch (IOException)
            {
                return IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-read-failed");
            }

            return BuildVerifiedResult(hFile, canonicalPath, sha256, fileInfo, extendedVolumeSerial, fileId128);
        }
    }

    public IdentityVerificationResult<VerifiedExecutableIdentity> Verify(string claimedPath, string claimedSha256) =>
        Verify(claimedPath, claimedPath, claimedSha256);

    public IdentityVerificationResult<VerifiedExecutableIdentity> Verify(
        string filePath,
        string claimedPath,
        string claimedSha256)
    {
        if (!_isWindows())
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Unsupported("windows-platform-required");

        if (string.IsNullOrWhiteSpace(filePath) || filePath.IndexOf('\0') >= 0)
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-invalid");

        if (string.IsNullOrWhiteSpace(claimedPath) || claimedPath.IndexOf('\0') >= 0)
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-mismatch");

        var resolved = Resolve(filePath);
        if (!resolved.IsTrusted || resolved.Evidence is null)
            return resolved;

        return ValidateClaims(resolved, claimedPath, claimedSha256);
    }

    public Task<IdentityVerificationResult<VerifiedExecutableIdentity>> VerifyAsync(
        string claimedPath,
        string claimedSha256,
        CancellationToken cancellationToken = default) =>
        VerifyAsync(claimedPath, claimedPath, claimedSha256, cancellationToken);

    public async Task<IdentityVerificationResult<VerifiedExecutableIdentity>> VerifyAsync(
        string filePath,
        string claimedPath,
        string claimedSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isWindows())
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Unsupported("windows-platform-required");

        if (string.IsNullOrWhiteSpace(filePath) || filePath.IndexOf('\0') >= 0)
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-invalid");

        if (string.IsNullOrWhiteSpace(claimedPath) || claimedPath.IndexOf('\0') >= 0)
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-mismatch");

        if (!TryOpenAndInspect(filePath, out SafeFileHandle? hFile, out string canonicalPath,
                out var fileInfo, out var extendedVolumeSerial, out var fileId128, out var failureResult))
        {
            return failureResult!;
        }

        using (hFile)
        {
            string sha256;
            using var stream = new FileStream(hFile, FileAccess.Read);
            try
            {
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hashBytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-access-denied");
            }
            catch (IOException)
            {
                return IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-read-failed");
            }

            var resolved = BuildVerifiedResult(hFile, canonicalPath, sha256, fileInfo, extendedVolumeSerial, fileId128);
            if (!resolved.IsTrusted || resolved.Evidence is null)
                return resolved;

            return ValidateClaims(resolved, claimedPath, claimedSha256);
        }
    }

    private bool TryOpenAndInspect(
        string filePath,
        [NotNullWhen(true)] out SafeFileHandle? handle,
        out string canonicalPath,
        out WindowsSecurityApis.BY_HANDLE_FILE_INFORMATION fileInfo,
        out ulong? extendedVolumeSerial,
        out string? fileId128,
        [NotNullWhen(false)] out IdentityVerificationResult<VerifiedExecutableIdentity>? failureResult)
    {
        handle = null;
        canonicalPath = string.Empty;
        fileInfo = default;
        extendedVolumeSerial = null;
        fileId128 = null;
        failureResult = null;

        if (!_isWindows())
        {
            failureResult = IdentityVerificationResult<VerifiedExecutableIdentity>.Unsupported("windows-platform-required");
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath) || filePath.IndexOf('\0') >= 0)
        {
            failureResult = IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-invalid");
            return false;
        }

        SafeFileHandle hFile = OpenAuthoritativeHandle(filePath, out int openError);
        if (hFile.IsInvalid)
        {
            hFile.Dispose();
            failureResult = MapOpenFailure(openError);
            return false;
        }

        if (!TryReadFileMetadata(hFile, out fileInfo, out extendedVolumeSerial, out fileId128))
        {
            hFile.Dispose();
            failureResult = IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-metadata-unavailable");
            return false;
        }

        if ((fileInfo.dwFileAttributes & FileAttributeDirectory) != 0)
        {
            hFile.Dispose();
            failureResult = IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-is-directory");
            return false;
        }

        if (!TryResolveCanonicalDosPath(hFile, out canonicalPath))
        {
            hFile.Dispose();
            failureResult = IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-path-resolution-failed");
            return false;
        }

        handle = hFile;
        return true;
    }

    private IdentityVerificationResult<VerifiedExecutableIdentity> BuildVerifiedResult(
        SafeFileHandle hFile,
        string canonicalPath,
        string sha256,
        WindowsSecurityApis.BY_HANDLE_FILE_INFORMATION fileInfo,
        ulong? extendedVolumeSerial,
        string? fileId128)
    {
        // Re-verify the handle's final path after hashing in case a rename occurred via FILE_SHARE_DELETE.
        if (!TryResolveCanonicalDosPath(hFile, out string postHashPath)
            || !string.Equals(canonicalPath, postHashPath, StringComparison.Ordinal))
        {
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-changed-during-verification");
        }

        ulong fileIndex = ((ulong)fileInfo.nFileIndexHigh << 32) | fileInfo.nFileIndexLow;
        long fileSizeBytes = ((long)fileInfo.nFileSizeHigh << 32) | fileInfo.nFileSizeLow;

        var evidence = new VerifiedExecutableIdentity(
            canonicalPath,
            sha256,
            fileInfo.dwVolumeSerialNumber,
            fileIndex,
            extendedVolumeSerial,
            fileId128,
            fileSizeBytes,
            _utcNow().ToUniversalTime());

        return IdentityVerificationResult<VerifiedExecutableIdentity>.Verified(evidence);
    }

    private static IdentityVerificationResult<VerifiedExecutableIdentity> ValidateClaims(
        IdentityVerificationResult<VerifiedExecutableIdentity> resolved,
        string claimedPath,
        string claimedSha256)
    {
        var evidence = resolved.Evidence!;
        if (!string.Equals(evidence.ExecutablePath, claimedPath, StringComparison.OrdinalIgnoreCase))
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-path-mismatch");

        if (string.IsNullOrWhiteSpace(claimedSha256)
            || !string.Equals(evidence.Sha256, claimedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return IdentityVerificationResult<VerifiedExecutableIdentity>.Rejected("executable-hash-mismatch");
        }

        return resolved;
    }

    private static SafeFileHandle OpenAuthoritativeHandle(string filePath, out int lastError)
    {
        SafeFileHandle hFile = WindowsSecurityApis.CreateFileW(
            filePath,
            WindowsSecurityApis.GENERIC_READ,
            WindowsSecurityApis.FILE_SHARE_READ | WindowsSecurityApis.FILE_SHARE_DELETE,
            IntPtr.Zero,
            WindowsSecurityApis.OPEN_EXISTING,
            WindowsSecurityApis.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        lastError = hFile.IsInvalid ? Marshal.GetLastWin32Error() : WindowsSecurityApis.ERROR_SUCCESS;
        return hFile;
    }

    private static IdentityVerificationResult<VerifiedExecutableIdentity> MapOpenFailure(int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound =>
            IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-not-found"),
        ErrorAccessDenied =>
            IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-access-denied"),
        ErrorSharingViolation =>
            IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable("executable-sharing-violation"),
        _ => IdentityVerificationResult<VerifiedExecutableIdentity>.Unavailable($"executable-open-failed:{error}")
    };

    private static bool TryReadFileMetadata(
        SafeFileHandle hFile,
        out WindowsSecurityApis.BY_HANDLE_FILE_INFORMATION fileInfo,
        out ulong? extendedVolumeSerial,
        out string? fileId128)
    {
        extendedVolumeSerial = null;
        fileId128 = null;

        if (!WindowsSecurityApis.GetFileInformationByHandle(hFile, out fileInfo))
            return false;

        int size = Marshal.SizeOf<WindowsSecurityApis.FILE_ID_INFO>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (WindowsSecurityApis.GetFileInformationByHandleEx(
                    hFile,
                    WindowsSecurityApis.FILE_INFO_BY_HANDLE_CLASS.FileIdInfo,
                    buffer,
                    (uint)size))
            {
                var idInfo = Marshal.PtrToStructure<WindowsSecurityApis.FILE_ID_INFO>(buffer);
                extendedVolumeSerial = idInfo.VolumeSerialNumber;
                if (idInfo.FileId.Identifier is { Length: 16 })
                    fileId128 = Convert.ToHexString(idInfo.FileId.Identifier);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return true;
    }

    private static bool TryResolveCanonicalDosPath(SafeFileHandle hFile, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        uint flags = WindowsSecurityApis.FILE_NAME_NORMALIZED | WindowsSecurityApis.VOLUME_NAME_DOS;

        var sb = new StringBuilder(InitialPathCapacity);
        uint length = WindowsSecurityApis.GetFinalPathNameByHandleW(hFile, sb, (uint)sb.Capacity, flags);
        if (length == 0)
            return false;

        if (length >= (uint)sb.Capacity)
        {
            sb = new StringBuilder(checked((int)length + 1));
            length = WindowsSecurityApis.GetFinalPathNameByHandleW(hFile, sb, (uint)sb.Capacity, flags);
            if (length == 0 || length >= (uint)sb.Capacity)
                return false;
        }

        canonicalPath = StripExtendedDosPrefix(sb.ToString(0, (int)length));
        return !string.IsNullOrWhiteSpace(canonicalPath);
    }

    internal static string StripExtendedDosPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[@"\\?\UNC\".Length..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path[@"\\?\".Length..];
        return path;
    }
}
